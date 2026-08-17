namespace KartPaymentService.Domain.Payments;

/// <summary>
/// ddd-model.md: nullable value object on <see cref="PaymentIntent"/>; presence of this record is
/// exactly what puts <see cref="PaymentIntentStatus"/> into <see cref="PaymentIntentStatus.Disputed"/>.
/// Deliberately not a repeatable/multi-valued history (ADR-0012's minimal "stop the bleeding" scope
/// — see ddd-model.md Modeling Decision #4).
/// </summary>
/// <summary>
/// No separate currency column - database-design.md's `payment_intents` schema has no
/// `chargeback_currency` field; a chargeback is always denominated in the original charge's own
/// <see cref="PaymentIntent.CapturedAmount"/> currency. <see cref="Amount"/> is deliberately a
/// plain <see cref="decimal"/>, not <see cref="Money"/> - there is nowhere on this row to store a
/// second currency, and this record is only ever constructed internally by
/// <see cref="PaymentIntent.MarkDisputed"/>, which has already checked the reported chargeback
/// amount's currency against the parent before this type is ever touched.
/// </summary>
public sealed record ChargebackRecord(ChargebackId ChargebackId, decimal Amount, string Reason, DateTimeOffset ReceivedAt);
