namespace KartPaymentService.Application.Common.Interfaces;

/// <summary>The write-side Unit of Work - `PaymentDbContext` is the implementation (EF Core's `DbContext` already is the Unit of Work, per ddd-cqrs-standards.md; no separate abstraction beyond this thin interface).</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task BeginTransactionAsync(CancellationToken cancellationToken);

    Task CommitTransactionAsync(CancellationToken cancellationToken);

    Task RollbackTransactionAsync(CancellationToken cancellationToken);
}
