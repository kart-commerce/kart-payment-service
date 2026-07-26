using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Models;
using MediatR;

namespace KartPaymentService.Application.Features.GetPaymentIntent;

/// <summary>PAY-4: api-contract.yaml `GET /v1/payments/{id}` - reads from the CQRS Mongo read side (`IPaymentIntentReadRepository`), never PostgreSQL directly, per the user's explicit CQRS requirement.</summary>
public sealed record GetPaymentIntentQuery(Guid PaymentIntentId) : IRequest<Result<PaymentIntentViewDto>>;
