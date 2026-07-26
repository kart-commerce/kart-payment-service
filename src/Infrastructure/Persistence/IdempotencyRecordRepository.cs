using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace KartPaymentService.Infrastructure.Persistence;

public sealed class IdempotencyRecordRepository(PaymentDbContext dbContext) : IIdempotencyRecordRepository
{
    public Task<IdempotencyRecord?> GetAsync(string idempotencyKey, IdempotencyEndpoint endpoint, CancellationToken cancellationToken) =>
        dbContext.IdempotencyRecords.FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey && r.Endpoint == endpoint, cancellationToken);

    public Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        dbContext.IdempotencyRecords.Add(record);
        return Task.CompletedTask;
    }
}
