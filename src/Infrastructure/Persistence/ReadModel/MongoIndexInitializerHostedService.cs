using KartPaymentService.Infrastructure.Persistence.ReadModel.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace KartPaymentService.Infrastructure.Persistence.ReadModel;

/// <summary>
/// Declares every index the read side's query shapes need, once at startup - idempotent (Mongo's
/// `createIndex` is a no-op if an equivalent index already exists). Fire-and-forget: a Mongo
/// outage at boot must not block the generic host from starting Kestrel.
/// </summary>
public sealed class MongoIndexInitializerHostedService : IHostedService
{
    private readonly PaymentReadDbContext _context;
    private readonly ILogger<MongoIndexInitializerHostedService> _logger;

    public MongoIndexInitializerHostedService(PaymentReadDbContext context, ILogger<MongoIndexInitializerHostedService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = DeclareIndexesAsync(cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Exposed (not private) so tests can await index creation deterministically instead of racing the fire-and-forget call in <see cref="StartAsync"/>.</summary>
    public async Task DeclareIndexesAsync(CancellationToken cancellationToken)
    {
        // GetPaymentIntent's (PAY-4) "look up by orderId" query shape - Order/Support-Agent lookup path.
        await CreateIndexAsync("payment_intents_read.orderId", () =>
            _context.PaymentIntents.Indexes.CreateOneAsync(
                new CreateIndexModel<PaymentIntentReadDocument>(
                    Builders<PaymentIntentReadDocument>.IndexKeys.Ascending(d => d.OrderId),
                    new CreateIndexOptions { Unique = true }),
                cancellationToken: cancellationToken));
    }

    private async Task CreateIndexAsync(string description, Func<Task<string>> createIndex)
    {
        try
        {
            await createIndex();
            _logger.LogInformation("Declared MongoDB read-model index: {Description}.", description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not declare MongoDB read-model index '{Description}' at startup.", description);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
