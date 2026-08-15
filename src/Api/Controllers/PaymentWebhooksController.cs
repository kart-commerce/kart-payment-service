using KartPaymentService.Api.Common;
using KartPaymentService.Api.Security;
using KartPaymentService.Application.Features.IngestGatewayWebhook;
using Kart.Shared.Observability;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Api.Controllers;

/// <summary>PAY-6/7/8: api-contract.yaml `POST /v1/payments/webhooks/{gateway}` - gateway-facing only, HMAC-signed (never reachable via the public API Gateway proxy).</summary>
[ApiController]
[Route("v1/payments/webhooks")]
[Authorize(AuthenticationSchemes = GatewaySignatureAuthenticationHandler.SchemeName)]
public sealed class PaymentWebhooksController(ISender sender, ILogger<PaymentWebhooksController> logger) : ControllerBase
{
    /// <summary>business-flows.md flow #6 "Payment Processing &amp; Fraud Check" - this is the gateway's own settlement-confirmation entry point (PAY-6/7/8), never checkout-triggered directly.</summary>
    private const string FlowName = "PaymentProcessingFraudCheck";

    [HttpPost("{gateway}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Ingest([FromRoute] string gateway, [FromBody] GatewayWebhookRequest request, CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);

        logger.LogInformation(
            "Stage {Stage}: gateway webhook request received from {Gateway}, event {GatewayEventId} ({EventType}) for payment intent {PaymentIntentId}",
            "GatewayWebhookRequestReceived",
            gateway,
            request.GatewayEventId,
            request.EventType,
            request.PaymentIntentId);

        var command = new IngestGatewayWebhookCommand(
            gateway,
            request.GatewayEventId,
            request.EventType,
            request.PaymentIntentId,
            request.TxnId,
            request.Reason,
            request.RefundId,
            request.Chargeback?.ChargebackId,
            request.Chargeback?.Amount.Amount,
            request.Chargeback?.Reason);

        logger.LogInformation("Stage {Stage}: dispatching IngestGatewayWebhookCommand for event {GatewayEventId}", "IngestGatewayWebhookCommandDispatched", request.GatewayEventId);

        var result = await sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : this.MapFailure(result.Error);
    }
}
