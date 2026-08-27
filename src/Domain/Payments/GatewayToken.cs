namespace KartPaymentService.Domain.Payments;

/// <summary>
/// Opaque, gateway-issued charge reference (requirement-spec Domain Invariant #4: never raw
/// card data). The redacting <see cref="ToString"/> is the actual domain reason this is a type
/// and not a bare string - accidental exposure of the raw token in a log line or an exception
/// message is exactly the failure mode this type exists to prevent.
/// </summary>
public readonly record struct GatewayToken
{
    public string Value { get; }

    public GatewayToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("GatewayToken cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Deliberately redacted - this value must never appear verbatim in logs/exceptions.</summary>
    public override string ToString() => "gwt_***";
}
