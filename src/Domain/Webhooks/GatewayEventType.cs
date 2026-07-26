namespace KartPaymentService.Domain.Webhooks;

/// <summary>
/// ddd-model.md: "exact enumeration is gateway-adapter-specific, not fixed here since concrete
/// gateway selection is deferred." Fixed to the four values api-contract.yaml's webhook endpoint
/// already enumerates, since v1 ships exactly one concrete adapter (requirement-spec Open Question
/// #2 resolution) - extend this enum, not replace it, if a second gateway adapter is ever added.
/// </summary>
public enum GatewayEventType
{
    ChargeSucceeded,
    ChargeFailed,
    RefundSucceeded,
    /// <summary>Additive extension beyond api-contract.yaml's original 4 values - a refund can definitively fail at the gateway too (e.g. destination account closed); `PaymentIntent.MarkRefundFailed` already supports it.</summary>
    RefundFailed,
    ChargebackReceived,
}
