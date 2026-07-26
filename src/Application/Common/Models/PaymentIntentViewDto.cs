namespace KartPaymentService.Application.Common.Models;

/// <summary>api-contract.yaml `components.schemas.PaymentIntentView`. `gatewayToken` is deliberately never a field here - it has no legitimate external consumer (database-design.md's Sensitive/PII Column Classification).</summary>
public sealed record PaymentIntentViewDto(
    Guid PaymentIntentId,
    string OrderId,
    string Status,
    MoneyDto CapturedAmount,
    string? TxnId,
    decimal TotalRefunded,
    bool Disputed,
    DateTimeOffset CreatedAt);
