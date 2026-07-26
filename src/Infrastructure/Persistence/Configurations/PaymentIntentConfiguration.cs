using KartPaymentService.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KartPaymentService.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="PaymentIntent"/> (and its owned <see cref="Refund"/> child collection) to `payment_intents`/`refunds`, verbatim from database-design.md.</summary>
public sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> builder)
    {
        builder.ToTable("payment_intents");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(p => p.OrderId).HasColumnName("order_id").IsRequired();
        builder.HasIndex(p => p.OrderId).IsUnique(); // "exactly one PaymentIntent per order" (database-design.md)

        builder.Property(p => p.GatewayToken).HasColumnName("gateway_token").IsRequired();
        builder.Property(p => p.TxnId).HasColumnName("txn_id");
        builder.Property(p => p.CapturedAmount).HasColumnName("captured_amount").HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(p => p.Currency).HasColumnName("currency").IsRequired();

        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasConversion(status => status.ToString().ToLowerInvariant(), value => ParseStatus(value))
            .IsRequired();

        builder.OwnsOne(p => p.Chargeback, chargeback =>
        {
            chargeback.Property(c => c.ChargebackId).HasColumnName("chargeback_id");
            chargeback.Property(c => c.Amount).HasColumnName("chargeback_amount").HasColumnType("numeric(12,2)");
            chargeback.Property(c => c.Reason).HasColumnName("chargeback_reason");
            chargeback.Property(c => c.ReceivedAt).HasColumnName("chargeback_received_at");
        });

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(p => p.UpdatedBy).HasColumnName("updated_by").IsRequired();

        // Refund child entity - same aggregate/transaction boundary as PaymentIntent (ddd-model.md
        // Modeling Decision #1), physically a separate table for cardinality reasons. Backed by
        // PaymentIntent's private `_refunds` field (EF Core's default field-then-property access
        // for a read-only collection navigation).
        builder.OwnsMany(p => p.Refunds, refund =>
        {
            refund.ToTable("refunds");
            refund.WithOwner().HasForeignKey(r => r.PaymentIntentId);
            refund.HasKey(r => r.Id);
            refund.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            refund.Property(r => r.PaymentIntentId).HasColumnName("payment_intent_id");
            refund.Property(r => r.Amount).HasColumnName("amount").HasColumnType("numeric(12,2)").IsRequired();
            refund.Property(r => r.Status)
                .HasColumnName("status")
                .HasConversion(status => status.ToString().ToLowerInvariant(), value => ParseRefundStatus(value))
                .IsRequired();
            refund.Property(r => r.RequestedAt).HasColumnName("requested_at").IsRequired();
            refund.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();
            refund.Property(r => r.CreatedBy).HasColumnName("created_by").IsRequired();
            refund.Property(r => r.UpdatedBy).HasColumnName("updated_by").IsRequired();

            // Supports `SUM(refunds.amount) <= captured_amount` ceiling check (database-design.md).
            refund.HasIndex(r => r.PaymentIntentId).HasDatabaseName("idx_refunds_intent_succeeded")
                .HasFilter("status = 'succeeded'");
        });

        builder.Navigation(p => p.Refunds).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static PaymentIntentStatus ParseStatus(string value) => Enum.Parse<PaymentIntentStatus>(value, ignoreCase: true);

    private static RefundStatus ParseRefundStatus(string value) => Enum.Parse<RefundStatus>(value, ignoreCase: true);
}
