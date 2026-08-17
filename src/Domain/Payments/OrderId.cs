namespace KartPaymentService.Domain.Payments;

/// <summary>
/// PAY-3's order reference - the raw id carried on kart-order-service's `OrderCreated` event.
/// This service treats it as an opaque foreign business key it never interprets structurally;
/// no assumption is made about the upstream id format. Protects exactly one invariant: it is
/// never blank. Uniqueness (one <see cref="PaymentIntent"/> per order) stays a database/
/// repository concern, not this type's.
/// </summary>
public readonly record struct OrderId
{
    public string Value { get; }

    public OrderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("OrderId cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value;
}
