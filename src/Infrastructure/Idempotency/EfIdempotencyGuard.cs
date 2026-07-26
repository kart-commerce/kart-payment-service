using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Idempotency;
using KartPaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KartPaymentService.Infrastructure.Idempotency;

/// <summary>
/// PAY-2: the DB-backed implementation of <see cref="IIdempotencyGuard"/>
/// (design-decisions.md "Idempotency Mechanism for Money-Moving POSTs"). `idempotency_keys`'
/// `(idempotency_key, endpoint)` PRIMARY KEY is the actual race-closer across two concurrent
/// requests with the same key - the lookup-then-insert here is a TOCTOU race like any other, so
/// this saves its own reservation immediately and, on losing that race to a concurrent request
/// that reserved the exact same key microseconds earlier, does NOT surface that as an error: it
/// waits briefly for the winner to confirm and replays its result, exactly the "retry while the
/// original attempt is still in flight" scenario design-decisions.md names as the harder half of
/// double-charge protection. Returning a hard conflict for a genuine concurrent duplicate would be
/// both wrong (it isn't a payload mismatch) and exactly the kind of race the platform's "never
/// double-charge" requirement exists to close.
/// </summary>
public sealed class EfIdempotencyGuard(PaymentDbContext dbContext, TimeProvider timeProvider) : IIdempotencyGuard
{
    private const string PostgresUniqueViolationSqlState = "23505";
    private const int MaxAttempts = 40;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(50);

    public async Task<IdempotencyReservation> ReserveOrReplayAsync(string idempotencyKey, IdempotencyEndpoint endpoint, string requestPayloadJson, string actingPrincipal, CancellationToken cancellationToken)
    {
        var requestPayloadHash = Hash(requestPayloadJson);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var existing = await dbContext.IdempotencyRecords
                .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey && r.Endpoint == endpoint, cancellationToken);

            if (existing is not null && existing.IsLive(now))
            {
                if (!existing.MatchesPayload(requestPayloadHash))
                {
                    return new IdempotencyReservation(IdempotencyOutcome.Conflict, null);
                }

                if (existing.StoredResponse is not null)
                {
                    return new IdempotencyReservation(IdempotencyOutcome.ReplayHit, existing.StoredResponse);
                }

                // Reserved (by us on a prior loop iteration, or by a concurrent request) but not
                // yet confirmed. Wait briefly and re-check rather than assuming it was abandoned -
                // this is exactly the concurrent-duplicate-request race, not an error condition.
                dbContext.ChangeTracker.Clear();
                await Task.Delay(PollDelay, cancellationToken);
                continue;
            }

            if (existing is not null)
            {
                // Expired past its 24h TTL - reused as a brand-new logical attempt (requirement-spec
                // Open Question #1 resolution), an UPDATE in place (Reopen), never a second INSERT -
                // see IdempotencyRecord.Reopen's own remarks for why a second row would violate the
                // (IdempotencyKey, Endpoint) primary key.
                existing.Reopen(requestPayloadHash, actingPrincipal, now);
            }
            else
            {
                dbContext.IdempotencyRecords.Add(IdempotencyRecord.Reserve(idempotencyKey, endpoint, requestPayloadHash, actingPrincipal, now));
            }

            try
            {
                // Saved immediately (not deferred to the caller's later SaveChangesAsync) - ddd-model.md's
                // Cross-Aggregate Interaction: "reserves an IdempotencyRecord first, in its own
                // transaction" - so a process crash mid-gateway-call still leaves a durable
                // reservation a concurrent retry will see.
                await dbContext.SaveChangesAsync(cancellationToken);
                return new IdempotencyReservation(IdempotencyOutcome.New, null);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
            {
                // Lost the race to a concurrent request that reserved this exact key first -
                // detach our failed attempt and loop back to see (and wait on) what they reserved.
                dbContext.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException($"Could not resolve an idempotency reservation for '{idempotencyKey}'/{endpoint} after {MaxAttempts} attempts.");
    }

    public async Task ConfirmAsync(string idempotencyKey, IdempotencyEndpoint endpoint, string storedResponseJson, CancellationToken cancellationToken)
    {
        var record = await dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey && r.Endpoint == endpoint, cancellationToken);

        record?.Confirm(storedResponseJson, timeProvider.GetUtcNow());
    }

    private static string Hash(string requestPayloadJson)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestPayloadJson));
        return Convert.ToHexString(bytes);
    }
}
