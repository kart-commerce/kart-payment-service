using System.Net;
using System.Net.Http.Json;
using KartPaymentService.IntegrationTests;
using Xunit;

namespace KartPaymentService.ContractTests;

/// <summary>Verifies PAY-3 against contracts/api-contract.yaml's `POST /v1/payments/charge` path - both that the contract still describes this shape and that the live endpoint matches it.</summary>
public sealed class ChargePaymentContractTests : IClassFixture<PaymentApiFactory>
{
    private const string ContractPath = "/v1/payments/charge";
    private readonly PaymentApiFactory _factory;

    public ChargePaymentContractTests(PaymentApiFactory factory) => _factory = factory;

    [Fact]
    public void Contract_DefinesChargePath_WithIdempotencyKeyHeaderAndExpectedResponses()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];
        Assert.True(paths.ContainsKey(ContractPath), $"api-contract.yaml no longer defines {ContractPath}");

        var postOp = (Dictionary<object, object>)((Dictionary<object, object>)paths[ContractPath])["post"];
        Assert.Equal("chargePayment", postOp["operationId"]);

        var parameters = (List<object>)postOp["parameters"];
        Assert.Contains(parameters.Cast<Dictionary<object, object>>(), p =>
            (string)p["name"] == "Idempotency-Key" && string.Equals(p["required"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

        var responses = (Dictionary<object, object>)postOp["responses"];
        Assert.True(responses.ContainsKey("200"));
        Assert.True(responses.ContainsKey("409"));
    }

    [Fact]
    public async Task LiveEndpoint_MissingIdempotencyKey_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, ContractPath)
        {
            Content = JsonContent.Create(new { orderId = "order-1", amount = new { amount = 10m, currency = "USD" }, gatewayToken = "tok_good" }),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LiveEndpoint_ValidRequest_MatchesDocumentedPaymentIntentViewShape()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, ContractPath)
        {
            Content = JsonContent.Create(new { orderId = $"order-{Guid.NewGuid():N}", amount = new { amount = 10m, currency = "USD" }, gatewayToken = "tok_good" }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        foreach (var required in new[] { "paymentIntentId", "orderId", "status", "capturedAmount" })
        {
            Assert.True(body.TryGetProperty(required, out _), $"PaymentIntentView response is missing required field '{required}'");
        }
    }
}
