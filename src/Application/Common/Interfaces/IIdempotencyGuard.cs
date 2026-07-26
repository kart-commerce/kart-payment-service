using KartPaymentService.Domain.Idempotency;

namespace KartPaymentService.Application.Common.Interfaces;

/// <summary>
/// PAY-2: the single mechanism `ChargePayment` and `RefundPayment` both call
/// (design-decisions.md "Idempotency Mechanism for Money-Moving POSTs"). `ReserveOrReplayAsync`
/// must be called, and must return <see cref="IdempotencyOutcome.New"/>, before any gateway call
/// or refund write is attempted; <see cref="ConfirmAsync"/> is called once that operation resolves.
/// </summary>
public interface IIdempotencyGuard
{
    Task<IdempotencyReservation> ReserveOrReplayAsync(string idempotencyKey, IdempotencyEndpoint endpoint, string requestPayloadJson, string actingPrincipal, CancellationToken cancellationToken);

    Task ConfirmAsync(string idempotencyKey, IdempotencyEndpoint endpoint, string storedResponseJson, CancellationToken cancellationToken);
}

public enum IdempotencyOutcome
{
    /// <summary>No live record for this (key, endpoint) - proceed with a new attempt.</summary>
    New,

    /// <summary>Identical-payload replay within the 24h TTL - return <see cref="IdempotencyReservation.StoredResponseJson"/> with no second gateway call.</summary>
    ReplayHit,

    /// <summary>Same key reused with a different request payload within the TTL - `409 Conflict`.</summary>
    Conflict,
}

public sealed record IdempotencyReservation(IdempotencyOutcome Outcome, string? StoredResponseJson);
