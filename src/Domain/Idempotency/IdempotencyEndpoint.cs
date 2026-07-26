namespace KartPaymentService.Domain.Idempotency;

/// <summary>database-design.md: `endpoint CHECK (endpoint IN ('charge', 'refund'))` - the `(idempotency_key, endpoint)` scope prevents a charge-key and a refund-key from colliding.</summary>
public enum IdempotencyEndpoint
{
    Charge,
    Refund,
}
