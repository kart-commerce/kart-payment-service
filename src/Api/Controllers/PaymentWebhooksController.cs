using KartPaymentService.Api.Common;
using KartPaymentService.Api.Security;
using KartPaymentService.Application.Features.IngestGatewayWebhook;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KartPaymentService.Api.Controllers;

/// <summary>PAY-6/7/8: api-contract.yaml `POST /v1/payments/webhooks/{gateway}` - gateway-facing only, HMAC-signed (never reachable via the public API Gateway proxy).</summary>
[ApiController]
[Route("v1/payments/webhooks")]
[Authorize(AuthenticationSchemes = GatewaySignatureAuthenticationHandler.SchemeName)]
public sealed class PaymentWebhooksController(ISender sender) : ControllerBase
{
    [HttpPost("{gateway}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Ingest([FromRoute] string gateway, [FromBody] GatewayWebhookRequest request, CancellationToken cancellationToken)
    {
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

        var result = await sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : this.MapFailure(result.Error);
    }
}
