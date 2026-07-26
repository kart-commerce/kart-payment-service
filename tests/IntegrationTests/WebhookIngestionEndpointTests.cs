using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KartPaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KartPaymentService.IntegrationTests;

/// <summary>PAY-6/7/8 end-to-end: signature verification, idempotent-by-gateway-event-id ingestion, and the chargeback dispute-hold, all against real Postgres.</summary>
public sealed class WebhookIngestionEndpointTests : IClassFixture<PaymentApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentApiFactory _factory;

    public WebhookIngestionEndpointTests(PaymentApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private static string Sign(string body) =>
        Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(PaymentApiFactory.SimulatedGatewaySigningSecret)).ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, object payload, bool validSignature = true)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/webhooks/simulated")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Gateway-Signature", validSignature ? Sign(body) : "not-a-real-signature");
        return await client.SendAsync(request);
    }

    private async Task<Guid> ChargeAndGetCompletedIntentIdAsync()
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/charge")
        {
            Content = JsonContent.Create(new { orderId = $"order-{Guid.NewGuid():N}", amount = new { amount = 100m, currency = "USD" }, gatewayToken = "tok_good" }),
        };
        request.Headers.Add("Idempotency-Key", $"charge-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);
        var view = await response.Content.ReadFromJsonAsync<PaymentIntentViewResponse>(JsonOptions);
        return view!.PaymentIntentId;
    }

    [Fact]
    public async Task Ingest_InvalidSignature_Returns401()
    {
        var client = CreateClient();

        var response = await PostWebhookAsync(client, new { gatewayEventId = "evt-1", eventType = "charge_succeeded", paymentIntentId = Guid.NewGuid() }, validSignature: false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ingest_ChargebackReceived_MarksIntentDisputed_AndBlocksFurtherRefunds()
    {
        var intentId = await ChargeAndGetCompletedIntentIdAsync();
        var client = CreateClient();

        var chargebackResponse = await PostWebhookAsync(client, new
        {
            gatewayEventId = $"evt-{Guid.NewGuid():N}",
            eventType = "chargeback_received",
            paymentIntentId = intentId,
            chargeback = new { chargebackId = "cb-1", amount = new { amount = 100m, currency = "USD" }, reason = "fraud" },
        });
        chargebackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var intent = await dbContext.PaymentIntents.SingleAsync(p => p.Id == intentId);
        intent.Status.Should().Be(Domain.Payments.PaymentIntentStatus.Disputed);

        var refundRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{intentId}/refund")
        {
            Content = JsonContent.Create(new { amount = new { amount = 10m, currency = "USD" } }),
            Headers = { { "Idempotency-Key", $"refund-{Guid.NewGuid():N}" } },
        };
        var refundResponse = await client.SendAsync(refundRequest);
        refundResponse.StatusCode.Should().Be(HttpStatusCode.Conflict, "ADR-0012: a disputed intent must reject any new refund attempt");
    }

    [Fact]
    public async Task Ingest_DuplicateGatewayEventId_IsIdempotentNoOp()
    {
        var intentId = await ChargeAndGetCompletedIntentIdAsync();
        var client = CreateClient();
        var gatewayEventId = $"evt-{Guid.NewGuid():N}";

        var payload = new { gatewayEventId, eventType = "chargeback_received", paymentIntentId = intentId, chargeback = new { chargebackId = "cb-2", amount = new { amount = 100m, currency = "USD" }, reason = "fraud" } };

        var first = await PostWebhookAsync(client, payload);
        var second = await PostWebhookAsync(client, payload);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "a redelivered webhook with the same gatewayEventId must be a recognized no-op, never an error");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var webhookEventCount = await dbContext.GatewayWebhookEvents.CountAsync(e => e.GatewayEventId == gatewayEventId);
        webhookEventCount.Should().Be(1);
    }

    private sealed record PaymentIntentViewResponse(Guid PaymentIntentId, string OrderId, string Status, string? TxnId);
}
