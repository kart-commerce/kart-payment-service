using FluentAssertions;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Idempotency;
using KartPaymentService.Infrastructure.Idempotency;
using KartPaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KartPaymentService.UnitTests.Infrastructure;

public sealed class EfIdempotencyGuardTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PaymentDbContext _dbContext;

    public EfIdempotencyGuardTests()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new PaymentDbContext(options);
    }

    private EfIdempotencyGuard CreateGuard(DateTimeOffset now) => new(_dbContext, new FakeTimeProvider(now));

    [Fact]
    public async Task ReserveOrReplayAsync_FirstAttempt_ReturnsNew()
    {
        var guard = CreateGuard(Now);

        var reservation = await guard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":10}", "system:test", CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        reservation.Outcome.Should().Be(IdempotencyOutcome.New);
    }

    [Fact]
    public async Task ReserveOrReplayAsync_SamePayload_AfterConfirm_ReplaysStoredResponse_WithoutANewGatewayCall()
    {
        var guard = CreateGuard(Now);
        await guard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":10}", "system:test", CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        await guard.ConfirmAsync("key-1", IdempotencyEndpoint.Charge, "{\"status\":\"completed\"}", CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var replay = await guard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":10}", "system:test", CancellationToken.None);

        replay.Outcome.Should().Be(IdempotencyOutcome.ReplayHit);
        replay.StoredResponseJson.Should().Be("{\"status\":\"completed\"}");
    }

    [Fact]
    public async Task ReserveOrReplayAsync_SameKeyDifferentPayload_ReturnsConflict()
    {
        var guard = CreateGuard(Now);
        await guard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":10}", "system:test", CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        await guard.ConfirmAsync("key-1", IdempotencyEndpoint.Charge, "{\"status\":\"completed\"}", CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var conflicting = await guard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":999}", "system:test", CancellationToken.None);

        conflicting.Outcome.Should().Be(IdempotencyOutcome.Conflict);
    }

    [Fact]
    public async Task ReserveOrReplayAsync_SameKeyValue_DifferentEndpoint_DoesNotCollide()
    {
        var guard = CreateGuard(Now);
        await guard.ReserveOrReplayAsync("shared-key", IdempotencyEndpoint.Charge, "{\"a\":1}", "system:test", CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var refundReservation = await guard.ReserveOrReplayAsync("shared-key", IdempotencyEndpoint.Refund, "{\"a\":1}", "system:test", CancellationToken.None);

        refundReservation.Outcome.Should().Be(IdempotencyOutcome.New, "(idempotency_key, endpoint) scoping must prevent a charge-key and a refund-key from colliding");
    }

    [Fact]
    public async Task ReserveOrReplayAsync_AfterTtlExpiry_IsReusableAsANewLogicalAttempt()
    {
        var guard = CreateGuard(Now);
        await guard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":10}", "system:test", CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        await guard.ConfirmAsync("key-1", IdempotencyEndpoint.Charge, "{\"status\":\"completed\"}", CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var laterGuard = CreateGuard(Now.AddHours(25)); // past the 24h TTL
        var reservation = await laterGuard.ReserveOrReplayAsync("key-1", IdempotencyEndpoint.Charge, "{\"amount\":10}", "system:test", CancellationToken.None);

        reservation.Outcome.Should().Be(IdempotencyOutcome.New);
    }

    public void Dispose() => _dbContext.Dispose();

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
