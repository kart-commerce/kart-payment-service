using Kart.Shared.Domain;
using MediatR;

namespace KartPaymentService.Application.Features.IngestGatewayWebhook;

/// <summary>
/// PAY-6/7/8: `POST /v1/payments/webhooks/{gateway}` - one vertical slice covering all three
/// tickets (they share the same endpoint and `GatewayWebhookEvent` dedup mechanism, per
/// tickets.md's own recommendation). `eventType` dispatches to the right
/// <see cref="Domain.Payments.PaymentIntent"/> transition. `TxnId`/`Reason`/`RefundId`/
/// `ChargebackId`/`ChargebackAmount`/`ChargebackReason` are each populated only for the
/// `eventType` they're relevant to - additive fields beyond api-contract.yaml's originally
/// sketched schema (which named no txnId/reason/amount fields at all for charge confirmation),
/// necessary since the BRD/contract's payload columns are illustrative, not exhaustive
/// (event-contract.md).
/// </summary>
public sealed record IngestGatewayWebhookCommand(
    string Gateway,
    string GatewayEventId,
    string EventType,
    Guid PaymentIntentId,
    string? TxnId,
    string? Reason,
    Guid? RefundId,
    string? ChargebackId,
    decimal? ChargebackAmount,
    string? ChargebackReason) : IRequest<Result>;
