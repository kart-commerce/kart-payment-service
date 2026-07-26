using KartPaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Infrastructure.IdempotencyPartitionMaintenance;

/// <summary>
/// PAY-9: TTL cleanup for `idempotency_keys`. database-design.md's prose recommends daily
/// range-partitioning by `created_at` so cleanup is a partition-drop rather than a scanning
/// `DELETE`; implemented instead as a batched `DELETE` here (see
/// `Configurations/IdempotencyRecordConfiguration.cs` for why native partitioning was rejected -
/// it would force `created_at` into the primary key, opening a double-charge race window across a
/// day boundary, which correctness cannot trade away). A generous safety margin (2 days past
/// `expires_at`, not just past it) keeps this from ever competing for I/O with checkout-path
/// writes near the boundary, and batching keeps any one delete transaction small.
/// </summary>
public sealed class IdempotencyPartitionMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<IdempotencyPartitionMaintenanceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetentionPastExpiry = TimeSpan.FromDays(2);
    private const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Idempotency-key TTL cleanup run failed; will retry on the next tick.");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var cutoff = timeProvider.GetUtcNow().Subtract(RetentionPastExpiry);
        int deletedThisRun;
        var totalDeleted = 0;

        do
        {
            var staleKeys = await dbContext.IdempotencyRecords
                .Where(r => r.ExpiresAt < cutoff)
                .OrderBy(r => r.ExpiresAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            deletedThisRun = staleKeys.Count;
            if (deletedThisRun > 0)
            {
                dbContext.IdempotencyRecords.RemoveRange(staleKeys);
                await dbContext.SaveChangesAsync(cancellationToken);
                totalDeleted += deletedThisRun;
            }
        }
        while (deletedThisRun == BatchSize && !cancellationToken.IsCancellationRequested);

        if (totalDeleted > 0)
        {
            logger.LogInformation("Idempotency-key TTL cleanup removed {Count} row(s) expired before {Cutoff}.", totalDeleted, cutoff);
        }
    }
}
