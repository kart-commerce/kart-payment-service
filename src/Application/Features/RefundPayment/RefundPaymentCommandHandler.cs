using System.Text.Json;
using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Common.Models;
using KartPaymentService.Domain.Idempotency;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Application.Features.RefundPayment;

/// <summary>
/// design-decisions.md "Concurrency Control for Refund Issuance" + "Saga Compensation Gating":
/// `PaymentIntent.RequestRefund` enforces the captured-amount ceiling, the dispute-hold, and the
/// terminal-state gate all in one place (Domain/Payments/PaymentIntent.cs) - but that in-memory
/// check alone cannot close a race between two concurrent refund requests against the same intent
/// loaded independently by two DbContext instances. `GetByIdForUpdateAsync` row-locks the intent
/// for the duration of an explicit transaction that covers only the check-then-insert itself, not
/// the (network) gateway call, which happens afterward with the lock already released.
/// </summary>
public sealed class RefundPaymentCommandHandler(
    IIdempotencyGuard idempotencyGuard,
    IPaymentGatewayAdapter gatewayAdapter,
    IPaymentIntentRepository paymentIntents,
    IUnitOfWork unitOfWork,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    ILogger<RefundPaymentCommandHandler> logger) : IRequestHandler<RefundPaymentCommand, Result<RefundViewDto>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// BRD §24.1.2: "Support Agent can refund, but only up to $X" - the BRD never states a number.
    /// Adopted as a defensible engineering default, configurable if a real figure is ever supplied.
    /// </summary>
    private const decimal SupportAgentRefundCapAmount = 500m;

    public async Task<Result<RefundViewDto>> Handle(RefundPaymentCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var requestPayloadJson = JsonSerializer.Serialize(new { request.PaymentIntentId, request.Amount, request.Currency }, SerializerOptions);

        var reservation = await idempotencyGuard.ReserveOrReplayAsync(request.IdempotencyKey, IdempotencyEndpoint.Refund, requestPayloadJson, actingPrincipal, cancellationToken);
        switch (reservation.Outcome)
        {
            case IdempotencyOutcome.Conflict:
                logger.LogWarning(
                    "Stage {Stage}: refund rejected, idempotency key {IdempotencyKey} was reused with a different request payload for payment intent {PaymentIntentId}",
                    "IdempotencyKeyConflict",
                    request.IdempotencyKey,
                    request.PaymentIntentId);
                return Result.Failure<RefundViewDto>(Error.Conflict("Idempotency-Key was reused with a different request payload."));
            case IdempotencyOutcome.ReplayHit:
                logger.LogInformation(
                    "Stage {Stage}: refund idempotency key {IdempotencyKey} replayed for payment intent {PaymentIntentId}, returning stored response",
                    "IdempotencyKeyReplayHit",
                    request.IdempotencyKey,
                    request.PaymentIntentId);
                return Result.Success(JsonSerializer.Deserialize<RefundViewDto>(reservation.StoredResponseJson!, SerializerOptions)!);
        }

        // ReserveOrReplayAsync already persisted the reservation itself, before returning
        // (EfIdempotencyGuard's own remarks).
        if (request.IsSupportAgentRequest && request.Amount > SupportAgentRefundCapAmount)
        {
            logger.LogWarning(
                "Stage {Stage}: refund rejected, Support Agent request for payment intent {PaymentIntentId} amount {Amount} {Currency} exceeds the cap of {CapAmount}",
                "RefundCapExceeded",
                request.PaymentIntentId,
                request.Amount,
                request.Currency,
                SupportAgentRefundCapAmount);
            return Result.Failure<RefundViewDto>(Error.Custom("refund_cap_exceeded", $"Support Agent refunds are capped at {SupportAgentRefundCapAmount} {request.Currency}."));
        }

        var now = timeProvider.GetUtcNow();
        Domain.Payments.Refund refund;
        string currency;

        // Row-locked section: only the ceiling-check-then-insert, never the external gateway call
        // (a lock held across network I/O is exactly what design-decisions.md's "briefly row-locks
        // ... for the duration of the refund-insert transaction" is careful to scope narrowly).
        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var intent = await paymentIntents.GetByIdForUpdateAsync(request.PaymentIntentId, cancellationToken);
            if (intent is null)
            {
                logger.LogWarning("Stage {Stage}: refund rejected, payment intent {PaymentIntentId} not found", "PaymentIntentNotFound", request.PaymentIntentId);
                await unitOfWork.RollbackTransactionAsync(cancellationToken);
                return Result.Failure<RefundViewDto>(Error.NotFound($"PaymentIntent '{request.PaymentIntentId}' not found."));
            }

            var refundResult = intent.RequestRefund(new Domain.Payments.Money(request.Amount, new Domain.Payments.CurrencyCode(request.Currency)), actingPrincipal, now);
            if (refundResult.IsFailure)
            {
                logger.LogWarning(
                    "Stage {Stage}: refund rejected for payment intent {PaymentIntentId}, reason {ErrorCode} — {ErrorMessage}",
                    "RefundRejected",
                    request.PaymentIntentId,
                    refundResult.Error.Code,
                    refundResult.Error.Message);
                await unitOfWork.RollbackTransactionAsync(cancellationToken);
                return Result.Failure<RefundViewDto>(refundResult.Error);
            }

            refund = refundResult.Value;
            currency = intent.CapturedAmount.Currency.Code;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
            logger.LogInformation(
                "Stage {Stage}: refund {RefundId} persisted for payment intent {PaymentIntentId}, amount {Amount} {Currency}",
                "RefundPersisted",
                refund.Id,
                request.PaymentIntentId,
                request.Amount,
                currency);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            throw;
        }

        // Submits the refund to the gateway for processing - a Declined outcome here means the
        // gateway rejected the submission itself (e.g. an invalid reference), which is terminal
        // immediately. Succeeded/Ambiguous both leave the refund Pending: settlement is
        // asynchronous relative to this call (api-contract.yaml's 202), confirmed later via
        // POST /v1/payments/webhooks/{gateway} (PAY-7), which is what actually publishes
        // RefundIssued.
        logger.LogInformation("Stage {Stage}: gateway refund submission started for refund {RefundId}", "GatewayRefundSubmissionStarted", refund.Id);
        var gatewayResult = await gatewayAdapter.RefundAsync(refund.PaymentIntentId.ToString(), request.Amount, currency, request.IdempotencyKey, cancellationToken);

        var dto = ToDto(refund, currency);

        if (gatewayResult.Outcome == Common.Interfaces.GatewayOutcome.Declined)
        {
            logger.LogInformation("Stage {Stage}: gateway refund submission declined for refund {RefundId}, reason {Reason}", "GatewayRefundSubmissionDeclined", refund.Id, gatewayResult.DeclineReason);
            var intentForUpdate = await paymentIntents.GetByIdAsync(request.PaymentIntentId, cancellationToken);
            intentForUpdate?.MarkRefundFailed(refund.Id, timeProvider.GetUtcNow());
            dto = ToDto(refund, currency) with { Status = "failed" };
        }
        else
        {
            logger.LogInformation("Stage {Stage}: gateway refund submission accepted for refund {RefundId}, left pending settlement confirmation", "GatewayRefundSubmissionAccepted", refund.Id);
        }

        await idempotencyGuard.ConfirmAsync(request.IdempotencyKey, IdempotencyEndpoint.Refund, JsonSerializer.Serialize(dto, SerializerOptions), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Stage {Stage}: refund {RefundId} for payment intent {PaymentIntentId} process completed with status {Status}",
            "RefundProcessCompleted",
            refund.Id,
            request.PaymentIntentId,
            dto.Status);

        return Result.Success(dto);
    }

    private static RefundViewDto ToDto(Domain.Payments.Refund refund, string currency) => new(
        refund.Id,
        refund.PaymentIntentId,
        new MoneyDto(refund.Amount, currency),
        refund.Status.ToString().ToLowerInvariant(),
        refund.RequestedAt);
}
