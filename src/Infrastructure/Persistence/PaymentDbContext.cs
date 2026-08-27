using KartPaymentService.Domain.Common;
using KartPaymentService.Domain.Idempotency;
using KartPaymentService.Domain.Payments;
using KartPaymentService.Domain.Webhooks;
using KartPaymentService.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KartPaymentService.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL write side (database-design.md) - the source of truth for all three aggregates.
/// There is deliberately no MongoDB anywhere in this DbContext; the read side is a separate,
/// eventually-consistent projection kept in sync via the outbox (see
/// <see cref="SaveChangesAsync(CancellationToken)"/> and Infrastructure/Messaging).
/// </summary>
public sealed class PaymentDbContext : DbContext
{
    private readonly ILogger<PaymentDbContext> _logger;

    // `logger` defaults to a no-op instance so PaymentDbContextFactory's design-time
    // (`dotnet ef migrations ...`) construction path, and any test that builds this DbContext
    // directly with only DbContextOptions, keep compiling unchanged - the runtime DI container
    // (Infrastructure/DependencyInjection.cs's AddDbContext) always supplies a real one via the
    // generic ILogger<T> registration every service gets for free.
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options, ILogger<PaymentDbContext>? logger = null) : base(options)
    {
        _logger = logger ?? NullLogger<PaymentDbContext>.Instance;
    }

    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<GatewayWebhookEvent> GatewayWebhookEvents => Set<GatewayWebhookEvent>();

    public DbSet<PaymentOutboxEvent> OutboxEvents => Set<PaymentOutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // PaymentIntentConfiguration also configures the owned `Refunds` collection (same
        // aggregate/transaction boundary, ddd-model.md) - there is no separate DbSet<Refund>.
        modelBuilder.ApplyConfiguration(new PaymentIntentConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new GatewayWebhookEventConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentOutboxEventConfiguration());
    }

    /// <summary>
    /// Converts every tracked aggregate's pending domain events into `payment_outbox_events` rows
    /// within this same call (design-decisions.md's Event Publication Reliability concern) - the
    /// write and "the event will eventually publish" commit atomically, never as a separate,
    /// unguarded publish step. The persisted+outbox-enqueued log below only logs ids/amounts/event
    /// types - never GatewayToken or any other payment-credential-adjacent field.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entitiesWithEvents = ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        var enqueuedByAggregate = new List<(Guid AggregateId, List<PaymentOutboxEvent> OutboxEvents)>();

        foreach (var entity in entitiesWithEvents)
        {
            var aggregateId = ResolveAggregateId(entity);
            var actingPrincipal = ResolveActingPrincipal(entity);
            var enqueued = new List<PaymentOutboxEvent>();

            foreach (var domainEvent in entity.DomainEvents)
            {
                var outboxEvent = PaymentOutboxEvent.FromDomainEvent(domainEvent, aggregateId, actingPrincipal);
                OutboxEvents.Add(outboxEvent);
                enqueued.Add(outboxEvent);
            }

            enqueuedByAggregate.Add((aggregateId, enqueued));
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var entity in entitiesWithEvents)
        {
            entity.ClearDomainEvents();
        }

        foreach (var (aggregateId, outboxEvents) in enqueuedByAggregate)
        {
            if (outboxEvents.Count == 0)
            {
                continue;
            }

            _logger.LogInformation(
                "Stage {Stage}: PaymentIntent {AggregateId} persisted, outbox event(s) {OutboxEventIds} ({EventTypes}) enqueued",
                "PaymentIntentPersistedOutboxEventEnqueued",
                aggregateId,
                string.Join(",", outboxEvents.Select(e => e.Id)),
                string.Join(",", outboxEvents.Select(e => e.EventType)));
        }

        return result;
    }

    private static Guid ResolveAggregateId(IHasDomainEvents entity) => entity switch
    {
        PaymentIntent intent => intent.Id,
        _ => Guid.Empty,
    };

    private static string ResolveActingPrincipal(IHasDomainEvents entity) => entity switch
    {
        PaymentIntent intent => intent.UpdatedBy,
        _ => "system:unknown",
    };
}
