using KartPaymentService.Api.Common;
using KartPaymentService.Application.Common.Models;
using KartPaymentService.Application.Features.ChargePayment;
using KartPaymentService.Application.Features.GetPaymentIntent;
using KartPaymentService.Application.Features.RefundPayment;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KartPaymentService.Api.Controllers;

[ApiController]
[Route("v1/payments")]
[Authorize]
public sealed class PaymentsController(ISender sender) : ControllerBase
{
    /// <summary>PAY-3: api-contract.yaml `POST /v1/payments/charge` - requires `Idempotency-Key`.</summary>
    [HttpPost("charge")]
    [ProducesResponseType(typeof(PaymentIntentViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PaymentIntentViewDto>> Charge(
        [FromBody] ChargePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new ChargePaymentCommand(request.OrderId, request.Amount.Amount, request.Amount.Currency, request.GatewayToken, idempotencyKey),
            cancellationToken);
        return this.ToActionResult<PaymentIntentViewDto, PaymentIntentViewDto>(result, dto => Ok(dto));
    }

    /// <summary>PAY-4: api-contract.yaml `GET /v1/payments/{id}` - reads from the CQRS Mongo read side.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PaymentIntentViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentIntentViewDto>> Get([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetPaymentIntentQuery(id), cancellationToken);
        return this.ToActionResult<PaymentIntentViewDto, PaymentIntentViewDto>(result, dto => Ok(dto));
    }

    /// <summary>PAY-5: api-contract.yaml `POST /v1/payments/{id}/refund` - requires `Idempotency-Key`. Serves both Support Agent tooling and Order's Saga-compensation caller.</summary>
    [HttpPost("{id:guid}/refund")]
    [ProducesResponseType(typeof(RefundViewDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefundViewDto>> Refund(
        [FromRoute] Guid id,
        [FromBody] RefundPaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // BRD §24.1.2: the refund-cap business rule applies only to the Support Agent path, never
        // to Order's own full-amount Saga-compensation call - resolved here from the JWT role
        // claim, since the API Gateway's coarse RBAC check can't make this data-dependent call.
        var isSupportAgentRequest = User.HasClaim("roles", "support_agent");

        var result = await sender.Send(
            new RefundPaymentCommand(id, request.Amount.Amount, request.Amount.Currency, idempotencyKey, isSupportAgentRequest),
            cancellationToken);
        return this.ToActionResult<RefundViewDto, RefundViewDto>(result, dto => Accepted(dto));
    }
}
