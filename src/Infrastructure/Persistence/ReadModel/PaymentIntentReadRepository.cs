using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Common.Models;
using MongoDB.Driver;

namespace KartPaymentService.Infrastructure.Persistence.ReadModel;

/// <summary>The CQRS query side of `GetPaymentIntent` (PAY-4) - reads exclusively from MongoDB, never PostgreSQL.</summary>
public sealed class PaymentIntentReadRepository(PaymentReadDbContext context) : IPaymentIntentReadRepository
{
    public async Task<PaymentIntentViewDto?> GetByIdAsync(Guid paymentIntentId, CancellationToken cancellationToken)
    {
        var document = await context.PaymentIntents.Find(d => d.Id == paymentIntentId).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToDto(document);
    }

    public async Task<PaymentIntentViewDto?> GetByOrderIdAsync(string orderId, CancellationToken cancellationToken)
    {
        var document = await context.PaymentIntents.Find(d => d.OrderId == orderId).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToDto(document);
    }

    private static PaymentIntentViewDto ToDto(Documents.PaymentIntentReadDocument document) => new(
        document.Id,
        document.OrderId,
        document.Status,
        new MoneyDto(document.CapturedAmount, document.Currency),
        document.TxnId,
        document.TotalRefunded,
        document.Disputed,
        document.CreatedAt);
}
