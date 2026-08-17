namespace KartPaymentService.Domain.Payments;

/// <summary>
/// Gateway-assigned transaction id, set exactly once when a <see cref="PaymentIntent"/>
/// completes - the `PaymentCompleted.txnId` field. A distinct type from <see cref="GatewayToken"/>
/// purely so the two opaque gateway strings (set at different points in the charge lifecycle,
/// meaning different things to the gateway) can never be swapped at a call site.
/// </summary>
public readonly record struct GatewayTransactionId
{
    public string Value { get; }

    public GatewayTransactionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("GatewayTransactionId cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value;
}
