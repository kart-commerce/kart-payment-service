using KartPaymentService.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace KartPaymentService.Infrastructure.PaymentGateway;

/// <summary>
/// Decorator pattern (coding-standards.md): wraps another <see cref="IPaymentGatewayAdapter"/>
/// with the resilience policy design-decisions.md mandates - bounded retry (3 attempts,
/// exponential backoff) restricted to <see cref="TransientGatewayException"/>, plus a circuit
/// breaker - kept fully independent of the 5x RabbitMQ event-redelivery tier. A definitive
/// Declined outcome is a plain return value, never an exception, so it is never retried. If every
/// retry is exhausted (or the circuit is open) the call resolves to <see cref="GatewayOutcome.Ambiguous"/>
/// rather than throwing - the caller must leave the `PaymentIntent` `Pending`, never speculatively
/// publish `PaymentFailed` (requirement-spec Open Question #9 resolution); resolution comes later
/// via the webhook path or PAY-10's reconciliation poll.
/// </summary>
public sealed class ResilientPaymentGatewayAdapter : IPaymentGatewayAdapter
{
    private readonly IPaymentGatewayAdapter _inner;
    private readonly ResiliencePipeline _pipeline;

    public ResilientPaymentGatewayAdapter(IPaymentGatewayAdapter inner, ILogger<ResilientPaymentGatewayAdapter> logger)
    {
        _inner = inner;
        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientGatewayException>(),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "Gateway call attempt {Attempt} failed transiently; retrying.", args.AttemptNumber + 1);
                    return ValueTask.CompletedTask;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientGatewayException>(),
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                OnOpened = args =>
                {
                    logger.LogCritical("Payment gateway circuit breaker OPEN for {BreakDuration} - gateway calls will fail fast.", args.BreakDuration);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public async Task<GatewayChargeResult> ChargeAsync(string gatewayToken, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(ct => new ValueTask<GatewayChargeResult>(_inner.ChargeAsync(gatewayToken, amount, currency, idempotencyKey, ct)), cancellationToken);
        }
        catch (TransientGatewayException)
        {
            return new GatewayChargeResult(GatewayOutcome.Ambiguous, null, "gateway_unreachable_after_retry");
        }
        catch (BrokenCircuitException)
        {
            return new GatewayChargeResult(GatewayOutcome.Ambiguous, null, "circuit_breaker_open");
        }
    }

    public async Task<GatewayRefundResult> RefundAsync(string paymentIntentReference, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(ct => new ValueTask<GatewayRefundResult>(_inner.RefundAsync(paymentIntentReference, amount, currency, idempotencyKey, ct)), cancellationToken);
        }
        catch (TransientGatewayException)
        {
            return new GatewayRefundResult(GatewayOutcome.Ambiguous, null, "gateway_unreachable_after_retry");
        }
        catch (BrokenCircuitException)
        {
            return new GatewayRefundResult(GatewayOutcome.Ambiguous, null, "circuit_breaker_open");
        }
    }

    /// <summary>Reconciliation is itself the resolution path for an ambiguous outcome - no further retry/circuit-breaker wrapping here.</summary>
    public Task<GatewayChargeResult> ReconcileChargeAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _inner.ReconcileChargeAsync(idempotencyKey, cancellationToken);
}
