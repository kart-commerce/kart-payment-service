using KartPaymentService.Domain.Idempotency;

namespace KartPaymentService.Application.Common.Interfaces;

public interface IIdempotencyRecordRepository
{
    Task<IdempotencyRecord?> GetAsync(string idempotencyKey, IdempotencyEndpoint endpoint, CancellationToken cancellationToken);

    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken);
}
