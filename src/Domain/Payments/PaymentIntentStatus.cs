namespace KartPaymentService.Domain.Payments;

/// <summary>
/// ddd-model.md: `Pending` is the only non-terminal state; `Completed`/`Failed` are terminal for
/// the charge itself; `Disputed` is reachable only from `Completed`. Allowed transitions are
/// exactly `Pending -> {Completed, Failed}` and `Completed -> Disputed` — enforced both here
/// (fast-fail in <see cref="PaymentIntent"/>) and by the `trg_payment_intents_status_guard`
/// Postgres trigger as a backstop against a coding bug (database-design.md).
/// </summary>
public enum PaymentIntentStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Disputed = 3,
}
