using KartPaymentService.Domain.Payments;

namespace KartPaymentService.Application.Common.Interfaces;

/// <summary>One repository per aggregate root - never a generic `IRepository&lt;T&gt;` (coding-standards.md).</summary>
public interface IPaymentIntentRepository
{
    Task<PaymentIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<PaymentIntent?> GetByOrderIdAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Row-locks the underlying `payment_intents` row (`SELECT ... FOR UPDATE`) for the duration
    /// of the caller's transaction - the actual race-closer behind design-decisions.md's
    /// "Concurrency Control for Refund Issuance" decision. Without this, two concurrent
    /// `RefundPayment` calls against the same intent could each load an independent in-memory copy,
    /// both pass the captured-amount-ceiling check, and both insert - exceeding the ceiling. Only
    /// `RefundPayment` needs this; `ChargePayment` creates a brand-new intent per order, whose only
    /// race is closed by the `idempotency_keys`/`payment_intents.order_id` unique constraints.
    /// </summary>
    Task<PaymentIntent?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(PaymentIntent paymentIntent, CancellationToken cancellationToken);

    /// <summary>Intents still `Pending` past the given age threshold - PAY-10's reconciliation poll candidates.</summary>
    Task<IReadOnlyList<PaymentIntent>> GetStalePendingAsync(TimeSpan olderThan, DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
