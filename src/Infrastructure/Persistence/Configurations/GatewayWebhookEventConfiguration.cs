using KartPaymentService.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KartPaymentService.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="GatewayWebhookEvent"/> to `gateway_webhook_events` (database-design.md).</summary>
public sealed class GatewayWebhookEventConfiguration : IEntityTypeConfiguration<GatewayWebhookEvent>
{
    public void Configure(EntityTypeBuilder<GatewayWebhookEvent> builder)
    {
        builder.ToTable("gateway_webhook_events");

        builder.HasKey(e => e.GatewayEventId);
        builder.Property(e => e.GatewayEventId).HasColumnName("gateway_event_id").ValueGeneratedNever();

        builder.Property(e => e.Gateway).HasColumnName("gateway").IsRequired();
        builder.Property(e => e.PaymentIntentId).HasColumnName("payment_intent_id").IsRequired();
        builder.Property(e => e.EventType)
            .HasColumnName("event_type")
            .HasConversion(type => type.ToString(), value => Enum.Parse<GatewayEventType>(value))
            .IsRequired();
        builder.Property(e => e.AppliedTransition).HasColumnName("applied_transition");
        builder.Property(e => e.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(e => e.ProcessedAt).HasColumnName("processed_at");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").IsRequired();

        builder.HasIndex(e => new { e.PaymentIntentId, e.ReceivedAt }).HasDatabaseName("idx_gateway_webhook_events_intent");
    }
}
