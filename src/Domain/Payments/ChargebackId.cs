namespace KartPaymentService.Domain.Payments;

/// <summary>Card-network/gateway dispute reference carried on a <see cref="ChargebackRecord"/>.</summary>
public readonly record struct ChargebackId
{
    public string Value { get; }

    public ChargebackId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("ChargebackId cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value;
}
