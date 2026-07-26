using FluentValidation;

namespace KartPaymentService.Application.Features.ChargePayment;

public sealed class ChargePaymentCommandValidator : AbstractValidator<ChargePaymentCommand>
{
    public ChargePaymentCommandValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.GatewayToken).NotEmpty();
        RuleFor(x => x.IdempotencyKey).NotEmpty();
    }
}
