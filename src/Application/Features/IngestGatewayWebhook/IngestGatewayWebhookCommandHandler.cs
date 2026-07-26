using Kart.Shared.Domain;
using KartPaymentService.Application.Common;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Webhooks;
using MediatR;

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
    TimeProvider timeProvider) : IRequestHandler<IngestGatewayWebhookCommand, Result>
{
    public async Task<Result> Handle(IngestGatewayWebhookCommand request, CancellationToken cancellationToken)
    {
        var existing = await webhookEvents.GetAsync(request.GatewayEventId, cancellationToken);
        if (existing is not null)
        {
            return Result.Success(); // already processed - idempotent no-op
        }

        var eventType = ParseEventType(request.EventType);
        var now = timeProvider.GetUtcNow();

        var intent = await paymentIntents.GetByIdAsync(request.PaymentIntentId, cancellationToken);
        if (intent is null)
        {
            return Result.Failure(Error.NotFound($"PaymentIntent '{request.PaymentIntentId}' not found."));
        }

        var webhookEvent = GatewayWebhookEvent.Receive(request.GatewayEventId, request.Gateway, request.PaymentIntentId, eventType, now);
        await webhookEvents.AddAsync(webhookEvent, cancellationToken);

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
            return transitionResult;
        }

        webhookEvent.MarkProcessed(eventType.ToString(), now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private static GatewayEventType ParseEventType(string eventType) => eventType switch
    {
        "charge_succeeded" => GatewayEventType.ChargeSucceeded,
        "charge_failed" => GatewayEventType.ChargeFailed,
        "refund_succeeded" => GatewayEventType.RefundSucceeded,
        "refund_failed" => GatewayEventType.RefundFailed,
        "chargeback_received" => GatewayEventType.ChargebackReceived,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unrecognized gateway webhook eventType."),
    };
}
