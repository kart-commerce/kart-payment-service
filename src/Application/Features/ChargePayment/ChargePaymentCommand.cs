using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Models;
using MediatR;

namespace KartPaymentService.Application.Features.ChargePayment;

/// <summary>PAY-3: api-contract.yaml `POST /v1/payments/charge` - requires `Idempotency-Key` (design-decisions.md's "Idempotency Mechanism for Money-Moving POSTs"). Also the code path the `OrderCreated` consumer calls, deriving its own deterministic key from `(orderId, "charge")`.</summary>
public sealed record ChargePaymentCommand(string OrderId, decimal Amount, string Currency, string GatewayToken, string IdempotencyKey) : IRequest<Result<PaymentIntentViewDto>>;
