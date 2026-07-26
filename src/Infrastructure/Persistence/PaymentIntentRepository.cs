using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace KartPaymentService.Infrastructure.Persistence;

public sealed class PaymentIntentRepository(PaymentDbContext dbContext) : IPaymentIntentRepository
{
    public Task<PaymentIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.PaymentIntents.Include(p => p.Refunds).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<PaymentIntent?> GetByOrderIdAsync(string orderId, CancellationToken cancellationToken) =>
        dbContext.PaymentIntents.Include(p => p.Refunds).FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);

    public async Task<PaymentIntent?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        // Must run inside the caller's already-open transaction (IUnitOfWork.BeginTransactionAsync)
        // - the lock this acquires is held only for that transaction's lifetime. The row's result
        // is discarded; only the server-side lock side effect matters. The subsequent EF query
        // below then reads (and the caller mutates/saves) that same now-locked row.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM payment_intents WHERE id = {id} FOR UPDATE", cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public Task AddAsync(PaymentIntent paymentIntent, CancellationToken cancellationToken)
    {
        dbContext.PaymentIntents.Add(paymentIntent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentIntent>> GetStalePendingAsync(TimeSpan olderThan, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var threshold = now.Subtract(olderThan);
        return dbContext.PaymentIntents
            .Where(p => p.Status == PaymentIntentStatus.Pending && p.CreatedAt < threshold)
            .OrderBy(p => p.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ContinueWith(t => (IReadOnlyList<PaymentIntent>)t.Result, cancellationToken);
    }
}
