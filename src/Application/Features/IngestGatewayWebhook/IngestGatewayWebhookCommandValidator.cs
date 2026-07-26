using FluentValidation;

namespace KartPaymentService.Application.Features.IngestGatewayWebhook;

public sealed class IngestGatewayWebhookCommandValidator : AbstractValidator<IngestGatewayWebhookCommand>
{
    private static readonly string[] KnownEventTypes = ["charge_succeeded", "charge_failed", "refund_succeeded", "refund_failed", "chargeback_received"];

    public IngestGatewayWebhookCommandValidator()
    {
        RuleFor(x => x.GatewayEventId).NotEmpty();
        RuleFor(x => x.PaymentIntentId).NotEmpty();
        RuleFor(x => x.EventType).NotEmpty().Must(t => KnownEventTypes.Contains(t)).WithMessage("eventType must be one of: " + string.Join(", ", KnownEventTypes));
        RuleFor(x => x.TxnId).NotEmpty().When(x => x.EventType == "charge_succeeded");
        RuleFor(x => x.RefundId).NotNull().When(x => x.EventType is "refund_succeeded" or "refund_failed");
        RuleFor(x => x.ChargebackId).NotEmpty().When(x => x.EventType == "chargeback_received");
        RuleFor(x => x.ChargebackAmount).NotNull().When(x => x.EventType == "chargeback_received");
    }
}
