using KartPaymentService.Application.Common;
using KartPaymentService.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Infrastructure.GatewayReconciliationJob;

/// <summary>
/// PAY-10: resolves a <see cref="Domain.Payments.PaymentIntent"/> left `Pending` because neither
/// the synchronous gateway call nor the webhook path ever confirmed a terminal state
/// (requirement-spec Open Question #9 resolution). Not a MediatR vertical slice - a pure
/// infrastructure job (tickets.md categorizes PAY-10 under `Infrastructure/GatewayReconciliationJob/`,
/// not `Application/Features/`), operating directly against the repository and gateway adapter.
///
/// Re-derives the same deterministic `order:{orderId}:charge` idempotency key the `OrderCreated`
/// consumer used to initiate the charge (architecture.md) - the primary/only production trigger
/// for `ChargePayment` in this saga design, per architecture.md's Sync/Async Resolution. A
/// still-Ambiguous outcome is left Pending for the next tick to retry.
/// </summary>
public sealed class GatewayReconciliationJobHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<GatewayReconciliationJobHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gateway reconciliation run failed; will retry on the next tick.");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    private async Task ReconcileBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var paymentIntents = scope.ServiceProvider.GetRequiredService<IPaymentIntentRepository>();
        var gatewayAdapter = scope.ServiceProvider.GetRequiredService<IPaymentGatewayAdapter>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var now = timeProvider.GetUtcNow();
        var stalePending = await paymentIntents.GetStalePendingAsync(StaleThreshold, now, BatchSize, cancellationToken);
        if (stalePending.Count == 0)
        {
            return;
        }

        foreach (var intent in stalePending)
        {
            var idempotencyKey = $"order:{intent.OrderId}:charge";
            var reconciliation = await gatewayAdapter.ReconcileChargeAsync(idempotencyKey, cancellationToken);

            switch (reconciliation.Outcome)
            {
                case GatewayOutcome.Succeeded:
                    intent.MarkCompleted(reconciliation.TxnId!, SystemPrincipals.GatewayReconciliationJob, now);
                    break;
                case GatewayOutcome.Declined:
                    intent.MarkFailed(reconciliation.DeclineReason ?? "reconciled_decline", SystemPrincipals.GatewayReconciliationJob, now);
                    break;
                case GatewayOutcome.Ambiguous:
                    logger.LogWarning("PaymentIntent {PaymentIntentId} still ambiguous after gateway reconciliation; will retry.", intent.Id);
                    break;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
