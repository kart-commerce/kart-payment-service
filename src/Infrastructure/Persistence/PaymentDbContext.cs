using KartPaymentService.Domain.Common;
using KartPaymentService.Domain.Idempotency;
using KartPaymentService.Domain.Payments;
using KartPaymentService.Domain.Webhooks;
using KartPaymentService.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace KartPaymentService.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL write side (database-design.md) - the source of truth for all three aggregates.
/// There is deliberately no MongoDB anywhere in this DbContext; the read side is a separate,
/// eventually-consistent projection kept in sync via the outbox (see
/// <see cref="SaveChangesAsync(CancellationToken)"/> and Infrastructure/Messaging).
/// </summary>
public sealed class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
    {
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
    /// unguarded publish step.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entitiesWithEvents = ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        foreach (var entity in entitiesWithEvents)
        {
            var aggregateId = ResolveAggregateId(entity);
            var actingPrincipal = ResolveActingPrincipal(entity);

            foreach (var domainEvent in entity.DomainEvents)
            {
                OutboxEvents.Add(PaymentOutboxEvent.FromDomainEvent(domainEvent, aggregateId, actingPrincipal));
            }
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var entity in entitiesWithEvents)
        {
            entity.ClearDomainEvents();
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
