using FluentAssertions;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Features.IngestGatewayWebhook;
using KartPaymentService.Domain.Payments;
using KartPaymentService.Domain.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartPaymentService.UnitTests.Features;

public sealed class IngestGatewayWebhookCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IGatewayWebhookEventRepository _webhookEvents = Substitute.For<IGatewayWebhookEventRepository>();
    private readonly IPaymentIntentRepository _paymentIntents = Substitute.For<IPaymentIntentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private IngestGatewayWebhookCommandHandler CreateHandler() => new(_webhookEvents, _paymentIntents, _unitOfWork, new FakeTimeProvider(Now), NullLogger<IngestGatewayWebhookCommandHandler>.Instance);

    [Fact]
    public async Task Handle_DuplicateGatewayEventId_IsIdempotentNoOp_NeverTouchesTheIntent()
    {
        _webhookEvents.GetAsync("evt-1", Arg.Any<CancellationToken>())
            .Returns(GatewayWebhookEvent.Receive("evt-1", "simulated", Guid.NewGuid(), GatewayEventType.ChargeSucceeded, Now));

        var handler = CreateHandler();
        var command = new IngestGatewayWebhookCommand("simulated", "evt-1", "charge_succeeded", Guid.NewGuid(), "txn_1", null, null, null, null, null);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _paymentIntents.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ChargeSucceeded_MarksIntentCompleted()
    {
        var intent = PaymentIntent.Create(Guid.NewGuid(), new OrderId("order-1"), new GatewayToken("tok_good"), new Money(50m, new CurrencyCode("USD")), "system:test", Now);
        _paymentIntents.GetByIdAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var command = new IngestGatewayWebhookCommand("simulated", "evt-2", "charge_succeeded", intent.Id, "txn_1", null, null, null, null, null);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        intent.Status.Should().Be(PaymentIntentStatus.Completed);
    }

    [Fact]
    public async Task Handle_ChargebackReceived_MarksIntentDisputed()
    {
        var intent = PaymentIntent.Create(Guid.NewGuid(), new OrderId("order-1"), new GatewayToken("tok_good"), new Money(50m, new CurrencyCode("USD")), "system:test", Now);
        intent.MarkCompleted(new GatewayTransactionId("txn_1"), "system:test", Now);
        _paymentIntents.GetByIdAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var command = new IngestGatewayWebhookCommand("simulated", "evt-3", "chargeback_received", intent.Id, null, null, null, "cb_1", 50m, "fraud");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        intent.Status.Should().Be(PaymentIntentStatus.Disputed);
    }

    [Fact]
    public async Task Handle_OutOfOrderTransition_ReturnsConflict_TreatedAsRecognizedNoOp()
    {
        var intent = PaymentIntent.Create(Guid.NewGuid(), new OrderId("order-1"), new GatewayToken("tok_good"), new Money(50m, new CurrencyCode("USD")), "system:test", Now);
        intent.MarkCompleted(new GatewayTransactionId("txn_1"), "system:test", Now); // already terminal
        _paymentIntents.GetByIdAsync(intent.Id, Arg.Any<CancellationToken>()).Returns(intent);

        var handler = CreateHandler();
        var command = new IngestGatewayWebhookCommand("simulated", "evt-4", "charge_failed", intent.Id, null, "late_failure", null, null, null, null);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue("edge-cases.md: a stale/out-of-order webhook transition must be rejected, not silently applied");
        result.Error.Code.Should().Be("conflict");
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
