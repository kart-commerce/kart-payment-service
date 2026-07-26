using System.Collections.Concurrent;
using KartPaymentService.Application.Common.Interfaces;

namespace KartPaymentService.Infrastructure.PaymentGateway;

/// <summary>
/// PAY-1: the one concrete <see cref="IPaymentGatewayAdapter"/> implementation shipped at launch
/// (requirement-spec Open Question #2 resolution - no real gateway credentials exist, so this
/// self-contained simulator exercises the full idempotency/retry/circuit-breaker/webhook/
/// reconciliation flow deterministically, without needing them). A real Stripe/Adyen adapter can
/// be swapped in later behind the same <see cref="IPaymentGatewayAdapter"/> interface with zero
/// change to Application/Domain.
///
/// Deterministic on the `gatewayToken`'s own content, so tests/manual smoke-testing can pick an
/// outcome by construction rather than randomly:
/// - contains "decline" -> a definitive decline (never retried)
/// - contains "timeout" -> throws <see cref="TransientGatewayException"/> every call (the
///   resilience decorator's retry+circuit-breaker is what a caller actually observes)
/// - anything else -> succeeds, with a deterministically derived `txnId`
///
/// Registered as a singleton - the in-memory `_charges` ledger is what makes
/// <see cref="ReconcileChargeAsync"/> able to "ask the gateway again" for an idempotency key this
/// process already saw, standing in for a real gateway's own durable transaction record.
/// </summary>
public sealed class SimulatedPaymentGatewayAdapter : Application.Common.Interfaces.IPaymentGatewayAdapter
{
    private readonly ConcurrentDictionary<string, GatewayChargeResult> _charges = new();

    public Task<GatewayChargeResult> ChargeAsync(string gatewayToken, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (gatewayToken.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            throw new TransientGatewayException($"Simulated gateway timeout for idempotency key '{idempotencyKey}'.");
        }

        var result = gatewayToken.Contains("decline", StringComparison.OrdinalIgnoreCase)
            ? new GatewayChargeResult(GatewayOutcome.Declined, null, "simulated_decline")
            : new GatewayChargeResult(GatewayOutcome.Succeeded, $"txn_{Guid.NewGuid():N}", null);

        _charges[idempotencyKey] = result;
        return Task.FromResult(result);
    }

    public Task<GatewayRefundResult> RefundAsync(string paymentIntentReference, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (paymentIntentReference.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            throw new TransientGatewayException($"Simulated gateway timeout for refund idempotency key '{idempotencyKey}'.");
        }

        var result = new GatewayRefundResult(GatewayOutcome.Succeeded, $"gwrefund_{Guid.NewGuid():N}", null);
        return Task.FromResult(result);
    }

    public Task<GatewayChargeResult> ReconcileChargeAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        // Simulates asking the gateway's own authoritative state for a charge this process
        // previously attempted - if we never recorded an outcome (the simulated "timeout" case),
        // resolve it as succeeded on reconciliation, standing in for "the charge actually went
        // through even though the synchronous call never got a response."
        var result = _charges.GetValueOrDefault(idempotencyKey)
            ?? new GatewayChargeResult(GatewayOutcome.Succeeded, $"txn_reconciled_{Guid.NewGuid():N}", null);
        return Task.FromResult(result);
    }
}
