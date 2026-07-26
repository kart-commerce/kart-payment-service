using FluentAssertions;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Infrastructure.PaymentGateway;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartPaymentService.UnitTests.Infrastructure;

public sealed class ResilientPaymentGatewayAdapterTests
{
    [Fact]
    public async Task ChargeAsync_DefiniteDecline_IsNeverRetried()
    {
        var inner = Substitute.For<IPaymentGatewayAdapter>();
        inner.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayChargeResult(GatewayOutcome.Declined, null, "insufficient_funds"));

        var decorator = new ResilientPaymentGatewayAdapter(inner, NullLogger<ResilientPaymentGatewayAdapter>.Instance);
        var result = await decorator.ChargeAsync("tok", 10m, "USD", "key-1", CancellationToken.None);

        result.Outcome.Should().Be(GatewayOutcome.Declined);
        await inner.Received(1).ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChargeAsync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var inner = Substitute.For<IPaymentGatewayAdapter>();
        var callCount = 0;
        inner.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new TransientGatewayException("simulated timeout");
                }

                return Task.FromResult(new GatewayChargeResult(GatewayOutcome.Succeeded, "txn_1", null));
            });

        var decorator = new ResilientPaymentGatewayAdapter(inner, NullLogger<ResilientPaymentGatewayAdapter>.Instance);
        var result = await decorator.ChargeAsync("tok", 10m, "USD", "key-1", CancellationToken.None);

        result.Outcome.Should().Be(GatewayOutcome.Succeeded);
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ChargeAsync_TransientFailureExhaustsRetries_ResolvesToAmbiguous_NeverThrows()
    {
        var inner = Substitute.For<IPaymentGatewayAdapter>();
        inner.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<GatewayChargeResult>>(_ => throw new TransientGatewayException("simulated timeout"));

        var decorator = new ResilientPaymentGatewayAdapter(inner, NullLogger<ResilientPaymentGatewayAdapter>.Instance);
        var result = await decorator.ChargeAsync("tok", 10m, "USD", "key-1", CancellationToken.None);

        result.Outcome.Should().Be(GatewayOutcome.Ambiguous, "requirement-spec Open Question #9: an unresolved outcome must never be reported as a definitive failure");
    }

    [Fact]
    public async Task SimulatedAdapter_DeclineToken_ReturnsDeclined()
    {
        var adapter = new SimulatedPaymentGatewayAdapter();

        var result = await adapter.ChargeAsync("tok_decline_card", 10m, "USD", "key-1", CancellationToken.None);

        result.Outcome.Should().Be(GatewayOutcome.Declined);
    }

    [Fact]
    public async Task SimulatedAdapter_TimeoutToken_ThrowsTransientGatewayException()
    {
        var adapter = new SimulatedPaymentGatewayAdapter();

        Func<Task> act = () => adapter.ChargeAsync("tok_timeout_card", 10m, "USD", "key-1", CancellationToken.None);

        await act.Should().ThrowAsync<TransientGatewayException>();
    }
}
