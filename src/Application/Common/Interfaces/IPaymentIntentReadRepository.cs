using KartPaymentService.Application.Common.Models;

namespace KartPaymentService.Application.Common.Interfaces;

/// <summary>
/// The CQRS query side - backed by MongoDB (sharded in production), never PostgreSQL directly.
/// `GetPaymentIntent` (PAY-4) is the only reader; kept eventually consistent via the outbox ->
/// RabbitMQ -> read-model-projection pipeline (Infrastructure/Messaging), per the user's explicit
/// CQRS requirement documented as a deviation in contracts/README.md.
/// </summary>
public interface IPaymentIntentReadRepository
{
    Task<PaymentIntentViewDto?> GetByIdAsync(Guid paymentIntentId, CancellationToken cancellationToken);

    Task<PaymentIntentViewDto?> GetByOrderIdAsync(string orderId, CancellationToken cancellationToken);
}
