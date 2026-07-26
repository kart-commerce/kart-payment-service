using KartPaymentService.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KartPaymentService.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="IdempotencyRecord"/> to `idempotency_keys` - PK `(idempotency_key, endpoint)`,
/// exactly as database-design.md's own DDL literally shows (that DDL sample was never actually
/// `PARTITION BY RANGE`, despite the accompanying prose recommending partitioning as the TTL
/// cleanup mechanism). Deliberately kept as a single, non-partitioned table here: Postgres
/// requires every unique constraint on a partitioned table to include the partition key column,
/// which would force this PK to become `(idempotency_key, endpoint, created_at)` - and two
/// concurrent charge attempts whose reservations land in different day-partitions (a narrow but
/// real boundary case, e.g. 23:59:59 vs. 00:00:01) would then both insert successfully, defeating
/// the exact-once double-charge guard this table exists to provide (requirement-spec Domain
/// Invariant #1, BRD §2.2). Correctness here outranks the cleanup-mechanism preference; TTL
/// cleanup is instead a batched `DELETE` (see `IdempotencyPartitionMaintenanceHostedService`),
/// not a partition-drop - documented in contracts/README.md.
/// </summary>
public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(r => new { r.IdempotencyKey, r.Endpoint });

        builder.Property(r => r.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(r => r.Endpoint)
            .HasColumnName("endpoint")
            .HasConversion(endpoint => endpoint.ToString().ToLowerInvariant(), value => Enum.Parse<IdempotencyEndpoint>(value, true))
            .IsRequired();

        builder.Property(r => r.RequestPayloadHash).HasColumnName("request_payload_hash").IsRequired();
        builder.Property(r => r.StoredResponse).HasColumnName("stored_response").HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(r => r.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(r => r.UpdatedBy).HasColumnName("updated_by").IsRequired();

        builder.HasIndex(r => r.ExpiresAt).HasDatabaseName("idx_idempotency_keys_expiry");
    }
}
