using Kart.Shared.Domain;

namespace KartPaymentService.Domain.Payments;

/// <summary>
/// event-contract.md's four published events. Field sets match the approved payloads exactly
/// (`orderId`/`txnId`, `orderId`/`reason`, `orderId`/`refundId`/`amount`,
/// `orderId`/`paymentIntentId`/`chargebackId`/`amount`/`reason`) plus `currency` alongside every
/// `amount` so downstream consumers never have to assume a currency - an additive, non-breaking
/// extension of the BRD's illustrative "(key fields)" columns, not a contract violation.
/// </summary>
/// <summary>Carries `capturedAmount`/`currency` alongside the BRD's illustrative `orderId`/`txnId` pair - needed because this is the first event ever published for a given intent (no `PaymentIntentCreated` event exists) and the CQRS read-model projection must seed its initial document from something.</summary>
public sealed record PaymentCompletedDomainEvent(Guid PaymentIntentId, string OrderId, string TxnId, decimal CapturedAmount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Same additive rationale as <see cref="PaymentCompletedDomainEvent"/> - this may also be the first (and only) event for an intent that never completes.</summary>
public sealed record PaymentFailedDomainEvent(Guid PaymentIntentId, string OrderId, string Reason, decimal CapturedAmount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RefundIssuedDomainEvent(Guid PaymentIntentId, Guid RefundId, string OrderId, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ChargebackReceivedDomainEvent(Guid PaymentIntentId, string OrderId, string ChargebackId, decimal Amount, string Currency, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;
