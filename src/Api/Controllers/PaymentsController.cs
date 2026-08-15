using KartPaymentService.Api.Common;
using KartPaymentService.Application.Common.Models;
using KartPaymentService.Application.Features.ChargePayment;
using KartPaymentService.Application.Features.GetPaymentIntent;
using KartPaymentService.Application.Features.RefundPayment;
using Kart.Shared.Observability;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Api.Controllers;

[ApiController]
[Route("v1/payments")]
[Authorize]
public sealed class PaymentsController(ISender sender, ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// business-flows.md flow #6 "Payment Processing &amp; Fraud Check" is the dedicated flow this
    /// controller's requests belong to - the OrderCreated-consumer-triggered charge attempt tags
    /// itself with flow #1's "NormalShoppingPurchaseJourney" instead (see
    /// Infrastructure/Messaging/OrderCreatedConsumerHostedService), since that entry point is
    /// specifically the checkout-triggered instance of this same generic charge process. Mirrors
    /// the event-type-based split already established in this service's own
    /// OutboxRelayHostedService.
    /// </summary>
    private const string FlowName = "PaymentProcessingFraudCheck";

    /// <summary>PAY-3: api-contract.yaml `POST /v1/payments/charge` - requires `Idempotency-Key`.</summary>
    [HttpPost("charge")]
    [ProducesResponseType(typeof(PaymentIntentViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PaymentIntentViewDto>> Charge(
        [FromBody] ChargePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);

        // Never logs request.GatewayToken - it's an opaque gateway-issued reference, never raw
        // card data (requirement-spec Domain Invariant #4).
        logger.LogInformation(
            "Stage {Stage}: charge request received for order {OrderId}, amount {Amount} {Currency}",
            "ChargeRequestReceived",
            request.OrderId,
            request.Amount.Amount,
            request.Amount.Currency);

        var command = new ChargePaymentCommand(request.OrderId, request.Amount.Amount, request.Amount.Currency, request.GatewayToken, idempotencyKey);
        var result = await sender.Send(command, cancellationToken);
        return this.ToActionResult<PaymentIntentViewDto, PaymentIntentViewDto>(result, dto => Ok(dto));
    }

    /// <summary>PAY-4: api-contract.yaml `GET /v1/payments/{id}` - reads from the CQRS Mongo read side.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PaymentIntentViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentIntentViewDto>> Get([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);

        logger.LogInformation("Stage {Stage}: get payment intent request received for {PaymentIntentId}", "GetPaymentIntentRequestReceived", id);
        var query = new GetPaymentIntentQuery(id);
        var result = await sender.Send(query, cancellationToken);
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
        using var _ = KartFlowContext.Push(FlowName);

        // BRD §24.1.2: the refund-cap business rule applies only to the Support Agent path, never
        // to Order's own full-amount Saga-compensation call - resolved here from the JWT role
        // claim, since the API Gateway's coarse RBAC check can't make this data-dependent call.
        var isSupportAgentRequest = User.HasClaim("roles", "support_agent");

        logger.LogInformation(
            "Stage {Stage}: refund request received for payment intent {PaymentIntentId}, amount {Amount} {Currency}, support-agent-initiated {IsSupportAgentRequest}",
            "RefundRequestReceived",
            id,
            request.Amount.Amount,
            request.Amount.Currency,
            isSupportAgentRequest);

        var command = new RefundPaymentCommand(id, request.Amount.Amount, request.Amount.Currency, idempotencyKey, isSupportAgentRequest);
        var result = await sender.Send(command, cancellationToken);
        return this.ToActionResult<RefundViewDto, RefundViewDto>(result, dto => Accepted(dto));
    }
}
