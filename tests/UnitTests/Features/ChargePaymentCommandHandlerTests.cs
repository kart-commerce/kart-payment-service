using FluentAssertions;
using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Features.ChargePayment;
using KartPaymentService.Domain.Idempotency;
using KartPaymentService.Domain.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartPaymentService.UnitTests.Features;

public sealed class ChargePaymentCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IIdempotencyGuard _idempotencyGuard = Substitute.For<IIdempotencyGuard>();
    private readonly IPaymentGatewayAdapter _gatewayAdapter = Substitute.For<IPaymentGatewayAdapter>();
    private readonly IPaymentIntentRepository _paymentIntents = Substitute.For<IPaymentIntentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();

    public ChargePaymentCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("system:test");
    }

    private ChargePaymentCommandHandler CreateHandler() => new(
        _idempotencyGuard, _gatewayAdapter, _paymentIntents, _unitOfWork, _currentPrincipal, new FakeTimeProvider(Now),
        NullLogger<ChargePaymentCommandHandler>.Instance);

    [Fact]
    public async Task Handle_SameKeyReplayed_ReturnsStoredResponseWithoutCallingGatewayAgain()
    {
        var storedResponse = """{"paymentIntentId":"11111111-1111-1111-1111-111111111111","orderId":"order-1","status":"completed","capturedAmount":{"amount":10,"currency":"USD"},"txnId":"txn_1","totalRefunded":0,"disputed":false,"createdAt":"2026-01-01T00:00:00+00:00"}""";
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Charge, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.ReplayHit, storedResponse));

        var handler = CreateHandler();
        var result = await handler.Handle(new ChargePaymentCommand("order-1", 10m, "USD", "tok_good", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OrderId.Should().Be("order-1");
        await _gatewayAdapter.DidNotReceive().ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SameKeyDifferentPayload_ReturnsConflict()
    {
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Charge, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.Conflict, null));

        var handler = CreateHandler();
        var result = await handler.Handle(new ChargePaymentCommand("order-1", 10m, "USD", "tok_good", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }

    [Fact]
    public async Task Handle_GatewaySucceeds_MarksIntentCompleted()
    {
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Charge, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.New, null));
        _gatewayAdapter.ChargeAsync("tok_good", 10m, "USD", "key-1", Arg.Any<CancellationToken>())
            .Returns(new GatewayChargeResult(GatewayOutcome.Succeeded, "txn_abc", null));

        var handler = CreateHandler();
        var result = await handler.Handle(new ChargePaymentCommand("order-1", 10m, "USD", "tok_good", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("completed");
        result.Value.TxnId.Should().Be("txn_abc");
        await _paymentIntents.Received(1).AddAsync(Arg.Any<PaymentIntent>(), Arg.Any<CancellationToken>());
        // The reservation's own save now happens inside IIdempotencyGuard (mocked here), so only
        // the final confirm+persist save is a call on IUnitOfWork from the handler itself.
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GatewayDeclines_MarksIntentFailed()
    {
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Charge, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.New, null));
        _gatewayAdapter.ChargeAsync("tok_decline", 10m, "USD", "key-1", Arg.Any<CancellationToken>())
            .Returns(new GatewayChargeResult(GatewayOutcome.Declined, null, "insufficient_funds"));

        var handler = CreateHandler();
        var result = await handler.Handle(new ChargePaymentCommand("order-1", 10m, "USD", "tok_decline", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a declined charge is still a successfully-recorded terminal outcome, not a handler failure");
        result.Value.Status.Should().Be("failed");
    }

    [Fact]
    public async Task Handle_GatewayAmbiguous_LeavesIntentPending_NeverSpeculativelyFailed()
    {
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Charge, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.New, null));
        _gatewayAdapter.ChargeAsync("tok_timeout", 10m, "USD", "key-1", Arg.Any<CancellationToken>())
            .Returns(new GatewayChargeResult(GatewayOutcome.Ambiguous, null, "gateway_unreachable_after_retry"));

        var handler = CreateHandler();
        var result = await handler.Handle(new ChargePaymentCommand("order-1", 10m, "USD", "tok_timeout", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("pending");
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
