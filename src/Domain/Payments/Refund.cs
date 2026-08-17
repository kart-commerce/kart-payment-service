namespace KartPaymentService.Domain.Payments;

/// <summary>
/// ddd-model.md: child entity of the <see cref="PaymentIntent"/> aggregate - never its own
/// aggregate root, since the captured-amount ceiling check must be atomic with the same
/// `payment_intents` row (Modeling Decision #1). Only ever constructed by
/// <see cref="PaymentIntent.RequestRefund"/> - no public factory here.
/// </summary>
public sealed class Refund
{
    public Guid Id { get; private set; }

    public Guid PaymentIntentId { get; private set; }

    /// <summary>
    /// No separate currency column (database-design.md's `refunds` table) - always denominated in
    /// the parent `PaymentIntent.CapturedAmount`'s currency. Deliberately a plain <see cref="decimal"/>,
    /// not <see cref="Money"/> - there is nowhere on this row to store a second currency, and this
    /// entity is only ever constructed internally by <see cref="PaymentIntent.RequestRefund"/>, which
    /// has already checked the requested amount's currency against the parent before this type is
    /// ever touched.
    /// </summary>
    public decimal Amount { get; private set; }

    public RefundStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string UpdatedBy { get; private set; } = string.Empty;

    /// <summary>EF Core materialization only.</summary>
    private Refund()
    {
    }

    internal Refund(Guid id, Guid paymentIntentId, decimal amount, string actingPrincipal, DateTimeOffset now)
    {
        Id = id;
        PaymentIntentId = paymentIntentId;
        Amount = amount;
        Status = RefundStatus.Pending;
        RequestedAt = now;
        UpdatedAt = now;
        CreatedBy = actingPrincipal;
        // database-design.md: "the webhook ingestion path is the only process that ever resolves
        // a refund's status after its initial insert" - default until settlement confirms.
        UpdatedBy = "system:payment-gateway-webhook-consumer";
    }

    internal void MarkSucceeded(DateTimeOffset now)
    {
        Status = RefundStatus.Succeeded;
        UpdatedAt = now;
        UpdatedBy = "system:payment-gateway-webhook-consumer";
    }

    internal void MarkFailed(DateTimeOffset now)
    {
        Status = RefundStatus.Failed;
        UpdatedAt = now;
        UpdatedBy = "system:payment-gateway-webhook-consumer";
    }
}
