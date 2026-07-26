namespace KartPaymentService.Application.Common.Models;

/// <summary>api-contract.yaml `components.schemas.RefundView`.</summary>
public sealed record RefundViewDto(
    Guid RefundId,
    Guid PaymentIntentId,
    MoneyDto Amount,
    string Status,
    DateTimeOffset RequestedAt);
