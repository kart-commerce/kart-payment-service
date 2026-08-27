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

        // OrderId/GatewayToken/TxnId are single-value Value Objects - HasConversion maps each
        // straight onto the same column the raw primitive used to occupy, so the physical schema
        // is untouched by the Domain/Payments Value Object refactor.
        builder.Property(p => p.OrderId)
            .HasColumnName("order_id")
            .HasConversion(v => v.Value, v => new OrderId(v))
            .IsRequired();
        builder.HasIndex(p => p.OrderId).IsUnique(); // "exactly one PaymentIntent per order" (database-design.md)

        builder.Property(p => p.GatewayToken)
            .HasColumnName("gateway_token")
            .HasConversion(v => v.Value, v => new GatewayToken(v))
            .IsRequired();

        builder.Property(p => p.TxnId)
            .HasColumnName("txn_id")
            .HasConversion(
                v => v.HasValue ? v.Value.Value : null,
                v => v == null ? (GatewayTransactionId?)null : new GatewayTransactionId(v));

        // Money is a complex property (EF Core 8) - Amount/Currency map onto the same two
        // pre-existing columns a bare decimal + string used to occupy; no schema change.
        builder.ComplexProperty(p => p.CapturedAmount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("captured_amount").HasColumnType("numeric(12,2)").IsRequired();
            money.Property(m => m.Currency)
                .HasConversion(v => v.Code, v => new CurrencyCode(v))
                .HasColumnName("currency")
                .IsRequired();
        });

        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasConversion(status => status.ToString().ToLowerInvariant(), value => ParseStatus(value))
            .IsRequired();

        builder.OwnsOne(p => p.Chargeback, chargeback =>
        {
            chargeback.Property(c => c.ChargebackId)
                .HasConversion(v => v.Value, v => new ChargebackId(v))
                .HasColumnName("chargeback_id");
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
