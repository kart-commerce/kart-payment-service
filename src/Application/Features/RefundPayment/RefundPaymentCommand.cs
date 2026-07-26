using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Models;
using MediatR;

namespace KartPaymentService.Application.Features.RefundPayment;

/// <summary>
/// PAY-5: api-contract.yaml `POST /v1/payments/{id}/refund` - serves two distinct callers through
/// one implementation (Support Agent via the public API, Order's Saga orchestrator via the
/// internal server for compensating refunds) - `IsSupportAgentRequest` is resolved by the calling
/// controller from the JWT role claim, since the refund-cap business rule (BRD §24.1.2) applies
/// only to the Support Agent path, never to Order's full-amount compensation call.
/// </summary>
public sealed record RefundPaymentCommand(Guid PaymentIntentId, decimal Amount, string Currency, string IdempotencyKey, bool IsSupportAgentRequest) : IRequest<Result<RefundViewDto>>;
