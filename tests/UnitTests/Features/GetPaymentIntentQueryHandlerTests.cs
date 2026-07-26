using FluentAssertions;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Common.Models;
using KartPaymentService.Application.Features.GetPaymentIntent;
using NSubstitute;
using Xunit;

namespace KartPaymentService.UnitTests.Features;

public sealed class GetPaymentIntentQueryHandlerTests
{
    private readonly IPaymentIntentReadRepository _readRepository = Substitute.For<IPaymentIntentReadRepository>();

    [Fact]
    public async Task Handle_ReadsFromTheCqrsReadSide_NotPostgres()
    {
        var id = Guid.NewGuid();
        var view = new PaymentIntentViewDto(id, "order-1", "completed", new MoneyDto(10m, "USD"), "txn_1", 0m, false, DateTimeOffset.UtcNow);
        _readRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(view);

        var handler = new GetPaymentIntentQueryHandler(_readRepository);
        var result = await handler.Handle(new GetPaymentIntentQuery(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(view);
    }

    [Fact]
    public async Task Handle_NotFoundInReadModel_ReturnsNotFound()
    {
        _readRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PaymentIntentViewDto?)null);

        var handler = new GetPaymentIntentQueryHandler(_readRepository);
        var result = await handler.Handle(new GetPaymentIntentQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
    }
}
