using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KartPaymentService.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="PaymentOutboxEvent"/> to `payment_outbox_events` - the Transactional Outbox pattern.</summary>
public sealed class PaymentOutboxEventConfiguration : IEntityTypeConfiguration<PaymentOutboxEvent>
{
    public void Configure(EntityTypeBuilder<PaymentOutboxEvent> builder)
    {
        builder.ToTable("payment_outbox_events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
        builder.Property(e => e.EventType).HasColumnName("event_type").HasColumnType("text").IsRequired();
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(e => e.PublishedAt).HasColumnName("published_at");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasColumnType("text").IsRequired();
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasColumnType("text").IsRequired();
        builder.Property(e => e.TraceParent).HasColumnName("trace_parent").HasColumnType("text");

        // OutboxRelayHostedService's "pending rows, oldest first" poll.
        builder.HasIndex(e => e.OccurredAt)
            .HasDatabaseName("idx_payment_outbox_unpublished")
            .HasFilter("published_at IS NULL");
    }
}
