using FluentAssertions;
using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Features.RefundPayment;
using KartPaymentService.Domain.Idempotency;
using KartPaymentService.Domain.Payments;
using NSubstitute;
using Xunit;

namespace KartPaymentService.UnitTests.Features;

public sealed class RefundPaymentCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IIdempotencyGuard _idempotencyGuard = Substitute.For<IIdempotencyGuard>();
    private readonly IPaymentGatewayAdapter _gatewayAdapter = Substitute.For<IPaymentGatewayAdapter>();
    private readonly IPaymentIntentRepository _paymentIntents = Substitute.For<IPaymentIntentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();

    public RefundPaymentCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("agent-1");
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Refund, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.New, null));
        _gatewayAdapter.RefundAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayRefundResult(GatewayOutcome.Succeeded, "gwref_1", null));
    }

    private RefundPaymentCommandHandler CreateHandler() => new(
        _idempotencyGuard, _gatewayAdapter, _paymentIntents, _unitOfWork, _currentPrincipal, new FakeTimeProvider(Now));

    private static PaymentIntent CompletedIntent(decimal capturedAmount = 100m)
    {
        var intent = PaymentIntent.Create(Guid.NewGuid(), "order-1", "tok_good", capturedAmount, "USD", "system:test", Now);
        intent.MarkCompleted("txn_1", "system:test", Now);
        return intent;
    }

    [Fact]
    public async Task Handle_SameKeyReplayed_ReturnsStoredResponseWithoutRelockingOrCallingGateway()
    {
        var storedResponse = """{"refundId":"11111111-1111-1111-1111-111111111111","paymentIntentId":"22222222-2222-2222-2222-222222222222","amount":{"amount":10,"currency":"USD"},"status":"succeeded","requestedAt":"2026-01-01T00:00:00+00:00"}""";
        _idempotencyGuard.ReserveOrReplayAsync(Arg.Any<string>(), IdempotencyEndpoint.Refund, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyReservation(IdempotencyOutcome.ReplayHit, storedResponse));

        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(Guid.NewGuid(), 10m, "USD", "key-1", false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _paymentIntents.DidNotReceive().GetByIdForUpdateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _gatewayAdapter.DidNotReceive().RefundAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SupportAgentOverCap_ReturnsRefundCapExceeded_WithoutTouchingTheIntent()
    {
        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(Guid.NewGuid(), 501m, "USD", "key-1", IsSupportAgentRequest: true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("refund_cap_exceeded");
        await _paymentIntents.DidNotReceive().GetByIdForUpdateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OrderServiceCompensationOverSupportAgentCap_IsNotCapped()
    {
        var intent = CompletedIntent(1000m);
        _paymentIntents.GetByIdForUpdateAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(intent.Id, 600m, "USD", "key-1", IsSupportAgentRequest: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("Order's Saga-compensation refund is a full-amount call, never subject to the Support Agent cap");
    }

    [Fact]
    public async Task Handle_ExceedsCapturedAmount_ReturnsConflict()
    {
        var intent = CompletedIntent(100m);
        _paymentIntents.GetByIdForUpdateAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(intent.Id, 150m, "USD", "key-1", false), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        await _gatewayAdapter.DidNotReceive().RefundAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AgainstDisputedIntent_ReturnsConflict()
    {
        var intent = CompletedIntent(100m);
        intent.MarkDisputed("cb_1", 100m, "fraud", Now, "system:webhook", Now);
        _paymentIntents.GetByIdForUpdateAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(intent.Id, 10m, "USD", "key-1", false), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }

    [Fact]
    public async Task Handle_WithinCapAndCeiling_Succeeds_AndAcceptsAsPending()
    {
        var intent = CompletedIntent(100m);
        _paymentIntents.GetByIdForUpdateAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(intent.Id, 40m, "USD", "key-1", true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("pending", "settlement is asynchronous - RefundIssued only publishes once the webhook confirms it");
        intent.Refunds.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_GatewayDeclinesSubmission_MarksRefundFailedImmediately()
    {
        var intent = CompletedIntent(100m);
        _paymentIntents.GetByIdForUpdateAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);
        _paymentIntents.GetByIdAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);
        _gatewayAdapter.RefundAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayRefundResult(GatewayOutcome.Declined, null, "invalid_reference"));

        var handler = CreateHandler();
        var result = await handler.Handle(new RefundPaymentCommand(intent.Id, 40m, "USD", "key-1", false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("failed");
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
