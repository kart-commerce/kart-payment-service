using System.Text.Json;
using Kart.Shared.Domain;
using KartPaymentService.Domain.Payments;

namespace KartPaymentService.Infrastructure.Persistence;

/// <summary>
/// database-design.md's transactional outbox row - one per domain event raised on
/// <see cref="PaymentIntent"/>, written in the same `SaveChangesAsync` call as the write it
/// describes (see <see cref="PaymentDbContext.SaveChangesAsync"/>). `EventType` is looked up
/// against `contracts/message-bus-manifest.json`'s `publishedEvents` by
/// <see cref="Messaging.OutboxRelayHostedService"/> to resolve the exchange/routing key - nothing
/// here is RabbitMQ-specific.
/// </summary>
public sealed class PaymentOutboxEvent : OutboxEventBase
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);

    public string CreatedBy { get; private set; } = string.Empty;
    public string UpdatedBy { get; private set; } = "system:payment-outbox-relay";

    private PaymentOutboxEvent()
    {
    }

    private PaymentOutboxEvent(Guid id, Guid aggregateId, string eventType, string payload, DateTimeOffset occurredAt)
        : base(id, aggregateId, eventType, payload, occurredAt)
    {
    }

    public static PaymentOutboxEvent FromDomainEvent(IDomainEvent domainEvent, Guid aggregateId, string actingPrincipal)
    {
        var (eventType, payload) = domainEvent switch
        {
            PaymentCompletedDomainEvent e => ("PaymentCompleted", (object)new
            {
                paymentIntentId = e.PaymentIntentId,
                orderId = e.OrderId,
                txnId = e.TxnId,
                capturedAmount = e.CapturedAmount,
                currency = e.Currency,
            }),
            PaymentFailedDomainEvent e => ("PaymentFailed", new
            {
                paymentIntentId = e.PaymentIntentId,
                orderId = e.OrderId,
                reason = e.Reason,
                capturedAmount = e.CapturedAmount,
                currency = e.Currency,
            }),
            RefundIssuedDomainEvent e => ("RefundIssued", new
            {
                paymentIntentId = e.PaymentIntentId,
                refundId = e.RefundId,
                orderId = e.OrderId,
                amount = e.Amount,
                currency = e.Currency,
            }),
            ChargebackReceivedDomainEvent e => ("ChargebackReceived", new
            {
                paymentIntentId = e.PaymentIntentId,
                orderId = e.OrderId,
                chargebackId = e.ChargebackId,
                amount = e.Amount,
                currency = e.Currency,
                reason = e.Reason,
            }),
            _ => throw new InvalidOperationException($"No outbox payload mapping for domain event '{domainEvent.GetType().Name}'."),
        };

        return new PaymentOutboxEvent(Guid.NewGuid(), aggregateId, eventType, JsonSerializer.Serialize(payload, PayloadSerializerOptions), domainEvent.OccurredAt)
        {
            CreatedBy = actingPrincipal,
        };
    }
}
