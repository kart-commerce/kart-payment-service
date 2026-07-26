using System.Text.Json;
using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Common.Models;
using KartPaymentService.Domain.Idempotency;
using MediatR;

namespace KartPaymentService.Application.Features.ChargePayment;

/// <summary>
/// design-decisions.md "Idempotency Mechanism for Money-Moving POSTs" +
/// ddd-model.md's Cross-Aggregate Interaction: reserve the idempotency record and persist it
/// (its own `SaveChangesAsync`) BEFORE the external gateway call - so a process crash mid-call
/// still leaves a durable reservation a concurrent retry will see - then make the call, then
/// persist the resulting `PaymentIntent` state and the confirmed idempotency record together in a
/// second `SaveChangesAsync`. This is the two-phase shape that actually closes the double-charge
/// race; a single end-of-handler save would leave the reservation only in-memory during the
/// (slowest, riskiest) external call.
/// </summary>
public sealed class ChargePaymentCommandHandler(
    IIdempotencyGuard idempotencyGuard,
    IPaymentGatewayAdapter gatewayAdapter,
    IPaymentIntentRepository paymentIntents,
    IUnitOfWork unitOfWork,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider) : IRequestHandler<ChargePaymentCommand, Result<PaymentIntentViewDto>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<PaymentIntentViewDto>> Handle(ChargePaymentCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var requestPayloadJson = JsonSerializer.Serialize(new { request.OrderId, request.Amount, request.Currency, request.GatewayToken }, SerializerOptions);

        var reservation = await idempotencyGuard.ReserveOrReplayAsync(request.IdempotencyKey, IdempotencyEndpoint.Charge, requestPayloadJson, actingPrincipal, cancellationToken);
        switch (reservation.Outcome)
        {
            case IdempotencyOutcome.Conflict:
                return Result.Failure<PaymentIntentViewDto>(Error.Conflict("Idempotency-Key was reused with a different request payload."));
            case IdempotencyOutcome.ReplayHit:
                return Result.Success(JsonSerializer.Deserialize<PaymentIntentViewDto>(reservation.StoredResponseJson!, SerializerOptions)!);
        }

        // ReserveOrReplayAsync already persisted the reservation itself, before returning - see
        // EfIdempotencyGuard's own remarks for why that save cannot be deferred to this handler.
        var now = timeProvider.GetUtcNow();
        var chargeResult = await gatewayAdapter.ChargeAsync(request.GatewayToken, request.Amount, request.Currency, request.IdempotencyKey, cancellationToken);

        var intent = Domain.Payments.PaymentIntent.Create(Guid.NewGuid(), request.OrderId, request.GatewayToken, request.Amount, request.Currency, actingPrincipal, now);

        switch (chargeResult.Outcome)
        {
            case Common.Interfaces.GatewayOutcome.Succeeded:
                intent.MarkCompleted(chargeResult.TxnId!, actingPrincipal, now);
                break;
            case Common.Interfaces.GatewayOutcome.Declined:
                intent.MarkFailed(chargeResult.DeclineReason ?? "declined", actingPrincipal, now);
                break;
            case Common.Interfaces.GatewayOutcome.Ambiguous:
                // Left Pending deliberately - requirement-spec Open Question #9 resolution. Never
                // speculatively publish PaymentFailed; PAY-10's reconciliation job or the webhook
                // path resolves this later.
                break;
        }

        await paymentIntents.AddAsync(intent, cancellationToken);

        var dto = ToDto(intent);
        await idempotencyGuard.ConfirmAsync(request.IdempotencyKey, IdempotencyEndpoint.Charge, JsonSerializer.Serialize(dto, SerializerOptions), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(dto);
    }

    private static PaymentIntentViewDto ToDto(Domain.Payments.PaymentIntent intent) => new(
        intent.Id,
        intent.OrderId,
        intent.Status.ToString().ToLowerInvariant(),
        new MoneyDto(intent.CapturedAmount, intent.Currency),
        intent.TxnId,
        intent.TotalRefunded,
        intent.Chargeback is not null,
        intent.CreatedAt);
}
