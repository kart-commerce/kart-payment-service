namespace KartPaymentService.Infrastructure.PaymentGateway;

/// <summary>Thrown by a gateway adapter for a transient/network-classified failure (timeout, gateway 5xx) - the only failure class the resilience decorator retries (design-decisions.md). A definitive decline is never represented as an exception.</summary>
public sealed class TransientGatewayException(string message) : Exception(message);
