using Kart.Shared.Domain;
using KartPaymentService.Application.Common;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Webhooks;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Application.Features.IngestGatewayWebhook;

/// <summary>
/// PAY-6/7/8: idempotent-by-`GatewayEventId` ingestion (edge-cases.md "Gateway Webhook Arriving
/// Out-of-Order or Duplicated") - a duplicate delivery of the same `GatewayEventId` is a no-op
/// before any `PaymentIntent` transition is even attempted. The monotonic-ordering guard itself
/// lives on `PaymentIntent` (Domain/Payments/PaymentIntent.cs) - both checks are required (ddd-model.md
/// Cross-Aggregate Interaction): this stops exact duplicates, `PaymentIntent`'s own guard stops
/// distinct-but-stale/out-of-order events this check alone would let through.
/// </summary>
public sealed class IngestGatewayWebhookCommandHandler(
    IGatewayWebhookEventRepository webhookEvents,
    IPaymentIntentRepository paymentIntents,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<IngestGatewayWebhookCommandHandler> logger) : IRequestHandler<IngestGatewayWebhookCommand, Result>
{
    public async Task<Result> Handle(IngestGatewayWebhookCommand request, CancellationToken cancellationToken)
    {
        var existing = await webhookEvents.GetAsync(request.GatewayEventId, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Stage {Stage}: gateway webhook event {GatewayEventId} already processed, idempotent no-op",
                "GatewayWebhookDuplicateNoOp",
                request.GatewayEventId);
            return Result.Success(); // already processed - idempotent no-op
        }

        var eventType = ParseEventType(request.EventType, request.GatewayEventId, logger);
        var now = timeProvider.GetUtcNow();

        var intent = await paymentIntents.GetByIdAsync(request.PaymentIntentId, cancellationToken);
        if (intent is null)
        {
            logger.LogWarning(
                "Stage {Stage}: gateway webhook rejected, payment intent {PaymentIntentId} not found for event {GatewayEventId}",
                "PaymentIntentNotFound",
                request.PaymentIntentId,
                request.GatewayEventId);
            return Result.Failure(Error.NotFound($"PaymentIntent '{request.PaymentIntentId}' not found."));
        }

        var webhookEvent = GatewayWebhookEvent.Receive(request.GatewayEventId, request.Gateway, request.PaymentIntentId, eventType, now);
        await webhookEvents.AddAsync(webhookEvent, cancellationToken);

        logger.LogInformation(
            "Stage {Stage}: applying gateway webhook transition {EventType} to payment intent {PaymentIntentId}",
            "GatewayWebhookTransitionBranch",
            eventType,
            request.PaymentIntentId);

        var transitionResult = eventType switch
        {
            GatewayEventType.ChargeSucceeded => intent.MarkCompleted(request.TxnId!, SystemPrincipals.PaymentGatewayWebhookConsumer, now),
            GatewayEventType.ChargeFailed => intent.MarkFailed(request.Reason ?? "gateway_reported_failure", SystemPrincipals.PaymentGatewayWebhookConsumer, now),
            GatewayEventType.RefundSucceeded => intent.MarkRefundSucceeded(request.RefundId!.Value, now),
            GatewayEventType.RefundFailed => intent.MarkRefundFailed(request.RefundId!.Value, now),
            GatewayEventType.ChargebackReceived => intent.MarkDisputed(request.ChargebackId!, request.ChargebackAmount!.Value, request.ChargebackReason ?? "chargeback", now, SystemPrincipals.PaymentGatewayWebhookConsumer, now),
            _ => Result.Failure(Error.Validation($"Unhandled gateway event type '{eventType}'.")),
        };

        if (transitionResult.IsFailure)
        {
            // edge-cases.md "Gateway Webhook Arriving Out-of-Order or Duplicated" - a distinct-but-
            // stale/out-of-order transition (e.g. a late charge_failed against an already-Completed
            // intent), not the exact-duplicate case caught above.
            logger.LogWarning(
                "Stage {Stage}: gateway webhook transition {EventType} rejected for payment intent {PaymentIntentId}, reason {ErrorCode} — {ErrorMessage}",
                "GatewayWebhookTransitionRejected",
                eventType,
                request.PaymentIntentId,
                transitionResult.Error.Code,
                transitionResult.Error.Message);
            return transitionResult;
        }

        webhookEvent.MarkProcessed(eventType.ToString(), now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Stage {Stage}: gateway webhook {GatewayEventId} processed for payment intent {PaymentIntentId}, transition {EventType} applied",
            "GatewayWebhookProcessCompleted",
            request.GatewayEventId,
            request.PaymentIntentId,
            eventType);

        return Result.Success();
    }

    private static GatewayEventType ParseEventType(string eventType, string gatewayEventId, ILogger logger) => eventType switch
    {
        "charge_succeeded" => GatewayEventType.ChargeSucceeded,
        "charge_failed" => GatewayEventType.ChargeFailed,
        "refund_succeeded" => GatewayEventType.RefundSucceeded,
        "refund_failed" => GatewayEventType.RefundFailed,
        "chargeback_received" => GatewayEventType.ChargebackReceived,
        _ => LogAndThrowUnrecognizedEventType(eventType, gatewayEventId, logger),
    };

    private static GatewayEventType LogAndThrowUnrecognizedEventType(string eventType, string gatewayEventId, ILogger logger)
    {
        logger.LogWarning(
            "Stage {Stage}: gateway webhook rejected, unrecognized eventType {EventType} for event {GatewayEventId}",
            "GatewayWebhookUnrecognizedEventType",
            eventType,
            gatewayEventId);
        throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unrecognized gateway webhook eventType.");
    }
}
