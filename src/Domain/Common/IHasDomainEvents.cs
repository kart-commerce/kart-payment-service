using Kart.Shared.Domain;

namespace KartPaymentService.Domain.Common;

/// <summary>
/// Lets Infrastructure's DbContext convert any tracked entity's pending domain events into outbox
/// rows with one <c>ChangeTracker.Entries&lt;IHasDomainEvents&gt;()</c> scan, regardless of which
/// base type (if any) the entity uses. <see cref="Payments.PaymentIntent"/> (a Guid-keyed
/// aggregate) satisfies this implicitly through its inherited <see cref="AggregateRoot"/> members;
/// <see cref="Idempotency.IdempotencyRecord"/> and <see cref="Webhooks.GatewayWebhookEvent"/> raise
/// no events, so they never implement this. Mirrors kart-offer-service's identical interface.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
