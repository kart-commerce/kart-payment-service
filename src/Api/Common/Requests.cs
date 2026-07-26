using KartPaymentService.Application.Common.Models;

namespace KartPaymentService.Api.Common;

/// <summary>api-contract.yaml `POST /v1/payments/charge` request body.</summary>
public sealed record ChargePaymentRequest(string OrderId, MoneyDto Amount, string GatewayToken);

/// <summary>api-contract.yaml `POST /v1/payments/{id}/refund` request body.</summary>
public sealed record RefundPaymentRequest(MoneyDto Amount);

/// <summary>api-contract.yaml `POST /v1/payments/webhooks/{gateway}` request body.</summary>
public sealed record GatewayWebhookRequest(
    string GatewayEventId,
    string EventType,
    Guid PaymentIntentId,
    string? TxnId,
    string? Reason,
    Guid? RefundId,
    ChargebackPayload? Chargeback);

public sealed record ChargebackPayload(string ChargebackId, MoneyDto Amount, string Reason);
