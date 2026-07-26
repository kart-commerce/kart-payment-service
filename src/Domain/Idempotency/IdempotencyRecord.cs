namespace KartPaymentService.Domain.Idempotency;

/// <summary>
/// ddd-model.md: separate aggregate root from <see cref="Payments.PaymentIntent"/> - the external
/// gateway call sits between reserving this record and confirming PaymentIntent's resulting state,
/// so the two writes structurally cannot share one transaction (Modeling Decision #2). Keyed on
/// the natural composite <c>(IdempotencyKey, Endpoint)</c>, not a synthetic Guid - no domain events
/// (pure infrastructure/idempotency-ledger aggregate).
/// </summary>
public sealed class IdempotencyRecord
{
    public string IdempotencyKey { get; private set; } = string.Empty;

    public IdempotencyEndpoint Endpoint { get; private set; }

    public string RequestPayloadHash { get; private set; } = string.Empty;

    /// <summary>Set once the gateway call / refund write resolves; null while the reserve-then-confirm cycle is still in flight.</summary>
    public string? StoredResponse { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>`CreatedAt` + 24h (requirement-spec's stated TTL default, design-decisions.md).</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string UpdatedBy { get; private set; } = string.Empty;

    /// <summary>EF Core materialization only.</summary>
    private IdempotencyRecord()
    {
    }

    private static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(24);

    public static IdempotencyRecord Reserve(string idempotencyKey, IdempotencyEndpoint endpoint, string requestPayloadHash, string actingPrincipal, DateTimeOffset now)
        => new()
        {
            IdempotencyKey = idempotencyKey,
            Endpoint = endpoint,
            RequestPayloadHash = requestPayloadHash,
            CreatedAt = now,
            ExpiresAt = now.Add(ReplayWindow),
            UpdatedAt = now,
            CreatedBy = actingPrincipal,
            UpdatedBy = actingPrincipal,
        };

    /// <summary>Identical-payload replay within the TTL returns the stored result; a differing hash is a conflict, never a silent overwrite.</summary>
    public bool MatchesPayload(string requestPayloadHash) => RequestPayloadHash == requestPayloadHash;

    public bool IsLive(DateTimeOffset now) => now < ExpiresAt;

    /// <summary>
    /// Reuses this row as a brand-new logical attempt once its TTL has expired - an UPDATE in
    /// place, never a second INSERT: the primary key is `(IdempotencyKey, Endpoint)` alone (see
    /// Configurations/IdempotencyRecordConfiguration.cs for why `CreatedAt` isn't part of it), so
    /// inserting a second row with the same key would violate that constraint regardless of how
    /// stale the first row is. Only valid to call when <see cref="IsLive"/> is already false.
    /// </summary>
    public void Reopen(string requestPayloadHash, string actingPrincipal, DateTimeOffset now)
    {
        RequestPayloadHash = requestPayloadHash;
        StoredResponse = null;
        CreatedAt = now;
        ExpiresAt = now.Add(ReplayWindow);
        UpdatedAt = now;
        CreatedBy = actingPrincipal;
        UpdatedBy = actingPrincipal;
    }

    public void Confirm(string storedResponseJson, DateTimeOffset now)
    {
        StoredResponse = storedResponseJson;
        UpdatedAt = now;
    }
}
