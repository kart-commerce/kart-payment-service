namespace KartPaymentService.Domain.Payments;

/// <summary>
/// ddd-model.md: nullable value object on <see cref="PaymentIntent"/>; presence of this record is
/// exactly what puts <see cref="PaymentIntentStatus"/> into <see cref="PaymentIntentStatus.Disputed"/>.
/// Deliberately not a repeatable/multi-valued history (ADR-0012's minimal "stop the bleeding" scope
/// — see ddd-model.md Modeling Decision #4).
/// </summary>
/// <summary>No separate currency column - database-design.md's `payment_intents` schema has no `chargeback_currency` field; a chargeback is always denominated in the original charge's own <see cref="PaymentIntent.Currency"/>.</summary>
public sealed record ChargebackRecord(string ChargebackId, decimal Amount, string Reason, DateTimeOffset ReceivedAt);
