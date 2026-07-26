namespace KartPaymentService.Application.Common.Models;

/// <summary>api-contract.yaml `components.schemas.Money`.</summary>
public sealed record MoneyDto(decimal Amount, string Currency);
