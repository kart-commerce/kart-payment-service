namespace KartPaymentService.Application.Common.Interfaces;

/// <summary>
/// PAY-1: gateway-agnostic adapter interface (requirement-spec Open Question #2 resolution) -
/// Application/Domain depend on this abstraction, Infrastructure provides exactly one concrete
/// implementation at launch (`SimulatedPaymentGatewayAdapter`; a real Stripe/Adyen adapter can be
/// swapped in later behind this same interface with no change above Infrastructure). Callers wrap
/// this adapter in a retry+circuit-breaker decorator (design-decisions.md) - this interface itself
/// carries no resilience policy.
/// </summary>
public interface IPaymentGatewayAdapter
{
    Task<GatewayChargeResult> ChargeAsync(string gatewayToken, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken);

    Task<GatewayRefundResult> RefundAsync(string paymentIntentReference, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>PAY-10: reconciliation poll for an intent left `Pending` after the synchronous path exhausted its retries without a definitive outcome.</summary>
    Task<GatewayChargeResult> ReconcileChargeAsync(string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// A definitive decline is terminal immediately; `Ambiguous` means the outcome is still unknown
/// after transient-retry is exhausted (timeout, gateway 5xx) - the caller must leave the
/// `PaymentIntent` `Pending` rather than speculatively firing `PaymentFailed`
/// (requirement-spec Open Question #9 resolution).
/// </summary>
public enum GatewayOutcome
{
    Succeeded,
    Declined,
    Ambiguous,
}

public sealed record GatewayChargeResult(GatewayOutcome Outcome, string? TxnId, string? DeclineReason);

public sealed record GatewayRefundResult(GatewayOutcome Outcome, string? GatewayRefundReference, string? DeclineReason);
