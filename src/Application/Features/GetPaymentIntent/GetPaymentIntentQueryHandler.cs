using Kart.Shared.Domain;
using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Application.Common.Models;
using MediatR;

namespace KartPaymentService.Application.Features.GetPaymentIntent;

public sealed class GetPaymentIntentQueryHandler(IPaymentIntentReadRepository readRepository) : IRequestHandler<GetPaymentIntentQuery, Result<PaymentIntentViewDto>>
{
    public async Task<Result<PaymentIntentViewDto>> Handle(GetPaymentIntentQuery request, CancellationToken cancellationToken)
    {
        var view = await readRepository.GetByIdAsync(request.PaymentIntentId, cancellationToken);
        return view is null
            ? Result.Failure<PaymentIntentViewDto>(Error.NotFound($"PaymentIntent '{request.PaymentIntentId}' not found."))
            : Result.Success(view);
    }
}
