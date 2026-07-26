namespace KartPaymentService.Application.Common;

/// <summary>Well-known non-HTTP-triggered actors (BRD §24.3) stamped as `createdBy`/`updatedBy` when a write is driven by a consumed event or internal process rather than an authenticated caller.</summary>
public static class SystemPrincipals
{
    public const string OrderSagaPaymentConsumer = "system:order-saga-payment-consumer";
    public const string PaymentGatewayWebhookConsumer = "system:payment-gateway-webhook-consumer";
    public const string GatewayReconciliationJob = "system:payment-gateway-reconciliation-job";
    public const string Unknown = "system:unknown";
}
