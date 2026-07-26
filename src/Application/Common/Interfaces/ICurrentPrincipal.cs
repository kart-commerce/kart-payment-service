namespace KartPaymentService.Application.Common.Interfaces;

/// <summary>
/// BRD §24.3 audit-actor resolution: the caller's own principal id (a Support Agent's JWT `sub`
/// for a manual refund) or a well-known `system:*` sentinel for a system-initiated mutation (the
/// `OrderCreated` consumer, Order's Saga-compensation refund call, the gateway-webhook consumer).
/// Passed explicitly into domain factory/mutator methods - never bound from a request DTO.
/// </summary>
public interface ICurrentPrincipal
{
    string ActingPrincipal { get; }
}
