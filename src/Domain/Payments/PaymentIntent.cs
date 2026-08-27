using Kart.Shared.Domain;
using KartPaymentService.Domain.Common;

namespace KartPaymentService.Domain.Payments;

/// <summary>
/// ddd-model.md's `PaymentIntent` aggregate root - the sole authoritative record of a charge's
/// state (requirement-spec Domain Invariant #2); never delegated to or duplicated inside the Order
/// Saga's own state machine. Guid-keyed, so it inherits <see cref="AggregateRoot"/> directly
/// (unlike a natural-key aggregate such as kart-offer-service's Coupon) and satisfies
/// <see cref="IHasDomainEvents"/> implicitly through AggregateRoot's own public members.
///
/// Every mutation here enforces the same invariants twice - once in this in-memory check (fast
/// fail, good error messages) and once more durably at the database (the `idx_refunds_intent_
/// succeeded` partial index + transactional check in the repository, and the
/// `trg_payment_intents_status_guard` trigger) - because the in-memory check alone cannot close a
/// race between two concurrent requests loaded from two different DbContext instances
/// (design-decisions.md "Concurrency Control for Refund Issuance").
/// </summary>
public sealed class PaymentIntent : AggregateRoot, IHasDomainEvents
{
    public OrderId OrderId { get; private set; }

    /// <summary>Opaque, gateway-issued reference. Never raw card data (requirement-spec Domain Invariant #4).</summary>
    public GatewayToken GatewayToken { get; private set; }

    /// <summary>Gateway-assigned transaction id, set exactly once on transition to Completed - the `PaymentCompleted.txnId` field.</summary>
    public GatewayTransactionId? TxnId { get; private set; }

    public Money CapturedAmount { get; private set; }

    public PaymentIntentStatus Status { get; private set; }

    public ChargebackRecord? Chargeback { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string UpdatedBy { get; private set; } = string.Empty;

    private readonly List<Refund> _refunds = new();

    public IReadOnlyCollection<Refund> Refunds => _refunds.AsReadOnly();

    public Money TotalRefunded =>
        new(_refunds.Where(r => r.Status == RefundStatus.Succeeded).Sum(r => r.Amount), CapturedAmount.Currency);

    /// <summary>EF Core materialization only.</summary>
    private PaymentIntent()
    {
    }

    private PaymentIntent(Guid id, OrderId orderId, GatewayToken gatewayToken, Money amount, string actingPrincipal, DateTimeOffset now)
    {
        Id = id;
        OrderId = orderId;
        GatewayToken = gatewayToken;
        CapturedAmount = amount;
        Status = PaymentIntentStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;
        CreatedBy = actingPrincipal;
        UpdatedBy = actingPrincipal;
    }

    /// <summary>
    /// PAY-3: opens a new charge attempt. Enforcing that an order only ever gets one PaymentIntent
    /// is the `payment_intents (order_id)` UNIQUE constraint's job (database-design.md), not this
    /// factory's - a retried `OrderCreated` delivery resolves via the idempotency ledger before
    /// ever reaching a second insert here.
    /// </summary>
    public static PaymentIntent Create(Guid id, OrderId orderId, GatewayToken gatewayToken, Money amount, string actingPrincipal, DateTimeOffset now)
        => new(id, orderId, gatewayToken, amount, actingPrincipal, now);

    /// <summary>Fired exactly once, only when status reaches Completed. Idempotent no-op on redelivery of the same outcome (edge-cases.md "Gateway Webhook Arriving Out-of-Order or Duplicated").</summary>
    public Result MarkCompleted(GatewayTransactionId txnId, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == PaymentIntentStatus.Completed)
        {
            return Result.Success();
        }

        if (Status != PaymentIntentStatus.Pending)
        {
            return Result.Failure(Error.Conflict($"Cannot mark PaymentIntent {Id} completed from status '{Status}'."));
        }

        Status = PaymentIntentStatus.Completed;
        TxnId = txnId;
        UpdatedAt = now;
        UpdatedBy = actingPrincipal;
        Raise(new PaymentCompletedDomainEvent(Id, OrderId.Value, txnId.Value, CapturedAmount.Amount, CapturedAmount.Currency.Code, now));
        return Result.Success();
    }

    /// <summary>Fired exactly once; never fired speculatively while the gateway outcome is still ambiguous (requirement-spec Open Question #9 resolution - callers leave the intent Pending instead of calling this).</summary>
    public Result MarkFailed(string reason, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == PaymentIntentStatus.Failed)
        {
            return Result.Success();
        }

        if (Status != PaymentIntentStatus.Pending)
        {
            return Result.Failure(Error.Conflict($"Cannot mark PaymentIntent {Id} failed from status '{Status}'."));
        }

        Status = PaymentIntentStatus.Failed;
        UpdatedAt = now;
        UpdatedBy = actingPrincipal;
        Raise(new PaymentFailedDomainEvent(Id, OrderId.Value, reason, CapturedAmount.Amount, CapturedAmount.Currency.Code, now));
        return Result.Success();
    }

    /// <summary>
    /// design-decisions.md "Concurrency Control for Refund Issuance": enforces the captured-amount
    /// ceiling and the dispute-hold in one place, counting Pending refunds toward the ceiling too
    /// (not just Succeeded) so two concurrent requests against the same loaded aggregate can't both
    /// pass this check - the transactional DB-level check the repository performs on insert is the
    /// actual race-closer across two different DbContext instances; this is the fast-fail half.
    /// Also the Saga Compensation Gating decision: a refund against a non-terminal intent is
    /// rejected, never fired unconditionally. The requested amount must be denominated in this
    /// intent's own captured currency - a currency-mismatched refund is rejected before it ever
    /// reaches the ceiling check.
    /// </summary>
    public Result<Refund> RequestRefund(Money amount, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == PaymentIntentStatus.Disputed)
        {
            return Result.Failure<Refund>(Error.Conflict($"PaymentIntent {Id} is disputed; new refunds are rejected (ADR-0012)."));
        }

        if (Status != PaymentIntentStatus.Completed)
        {
            return Result.Failure<Refund>(Error.Conflict($"PaymentIntent {Id} has not reached a terminal completed state; refund rejected."));
        }

        if (amount.Currency != CapturedAmount.Currency)
        {
            return Result.Failure<Refund>(Error.Conflict(
                $"Refund currency {amount.Currency} does not match PaymentIntent {Id}'s captured currency {CapturedAmount.Currency}."));
        }

        var committedOrPending = _refunds.Where(r => r.Status != RefundStatus.Failed).Sum(r => r.Amount);
        if (new Money(committedOrPending, CapturedAmount.Currency).Add(amount).IsGreaterThan(CapturedAmount))
        {
            return Result.Failure<Refund>(Error.Conflict($"Refund amount {amount} would exceed the captured amount {CapturedAmount} for PaymentIntent {Id}."));
        }

        var refund = new Refund(Guid.NewGuid(), Id, amount.Amount, actingPrincipal, now);
        _refunds.Add(refund);
        return Result.Success(refund);
    }

    /// <summary>Fired once per successful refund - one event per partial refund, not one per PaymentIntent (ddd-model.md).</summary>
    public Result MarkRefundSucceeded(Guid refundId, DateTimeOffset now)
    {
        var refund = _refunds.FirstOrDefault(r => r.Id == refundId);
        if (refund is null)
        {
            return Result.Failure(Error.NotFound($"Refund {refundId} not found on PaymentIntent {Id}."));
        }

        if (refund.Status == RefundStatus.Succeeded)
        {
            return Result.Success();
        }

        if (refund.Status == RefundStatus.Failed)
        {
            return Result.Failure(Error.Conflict($"Refund {refundId} already marked failed; cannot mark succeeded (out-of-order webhook)."));
        }

        refund.MarkSucceeded(now);
        Raise(new RefundIssuedDomainEvent(Id, refund.Id, OrderId.Value, refund.Amount, CapturedAmount.Currency.Code, now));
        return Result.Success();
    }

    /// <summary>No event published for a failed refund - only `RefundIssued` exists in event-contract.md; a failed refund is reflected in the read model via the same projection consumer re-reading Postgres state, never a platform-wide event.</summary>
    public Result MarkRefundFailed(Guid refundId, DateTimeOffset now)
    {
        var refund = _refunds.FirstOrDefault(r => r.Id == refundId);
        if (refund is null)
        {
            return Result.Failure(Error.NotFound($"Refund {refundId} not found on PaymentIntent {Id}."));
        }

        if (refund.Status == RefundStatus.Failed)
        {
            return Result.Success();
        }

        if (refund.Status == RefundStatus.Succeeded)
        {
            return Result.Failure(Error.Conflict($"Refund {refundId} already succeeded; cannot mark failed."));
        }

        refund.MarkFailed(now);
        return Result.Success();
    }

    /// <summary>ADR-0012: marks the intent disputed (blocking new refunds) and publishes ChargebackReceived. Idempotent on redelivery of the same chargeback notification. Always denominated in this intent's own <see cref="CapturedAmount"/> currency - database-design.md's schema has no separate chargeback-currency column, so a currency-mismatched chargeback is rejected rather than silently recorded.</summary>
    public Result MarkDisputed(ChargebackId chargebackId, Money amount, string reason, DateTimeOffset receivedAt, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == PaymentIntentStatus.Disputed)
        {
            return Result.Success();
        }

        if (Status != PaymentIntentStatus.Completed)
        {
            return Result.Failure(Error.Conflict("Disputed is only reachable from Completed (a charge that never completed cannot be charged back)."));
        }

        if (amount.Currency != CapturedAmount.Currency)
        {
            return Result.Failure(Error.Conflict(
                $"Chargeback currency {amount.Currency} does not match PaymentIntent {Id}'s captured currency {CapturedAmount.Currency}."));
        }

        Chargeback = new ChargebackRecord(chargebackId, amount.Amount, reason, receivedAt);
        Status = PaymentIntentStatus.Disputed;
        UpdatedAt = now;
        UpdatedBy = actingPrincipal;
        Raise(new ChargebackReceivedDomainEvent(Id, OrderId.Value, chargebackId.Value, amount.Amount, amount.Currency.Code, reason, now));
        return Result.Success();
    }
}
