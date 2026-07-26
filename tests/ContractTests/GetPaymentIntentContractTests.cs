using System.Net;
using System.Net.Http.Json;
using KartPaymentService.IntegrationTests;
using Xunit;

namespace KartPaymentService.ContractTests;

/// <summary>Verifies PAY-4 against contracts/api-contract.yaml's `GET /v1/payments/{id}` path.</summary>
public sealed class GetPaymentIntentContractTests : IClassFixture<PaymentApiFactory>
{
    private const string ContractPath = "/v1/payments/{id}";
    private readonly PaymentApiFactory _factory;

    public GetPaymentIntentContractTests(PaymentApiFactory factory) => _factory = factory;

    [Fact]
    public void Contract_DefinesGetPath_WithExpectedResponses()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];
        Assert.True(paths.ContainsKey(ContractPath), $"api-contract.yaml no longer defines {ContractPath}");

        var getOp = (Dictionary<object, object>)((Dictionary<object, object>)paths[ContractPath])["get"];
        Assert.Equal("getPaymentIntent", getOp["operationId"]);

        var responses = (Dictionary<object, object>)getOp["responses"];
        Assert.True(responses.ContainsKey("200"));
        Assert.True(responses.ContainsKey("404"));
    }

    [Fact]
    public async Task LiveEndpoint_UnknownId_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/v1/payments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LiveEndpoint_AfterCharge_EventuallyReflectsCompletedStateFromTheReadSide()
    {
        var client = _factory.CreateClient();
        var chargeRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/charge")
        {
            Content = JsonContent.Create(new { orderId = $"order-{Guid.NewGuid():N}", amount = new { amount = 10m, currency = "USD" }, gatewayToken = "tok_good" }),
        };
        chargeRequest.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var chargeResponse = await client.SendAsync(chargeRequest);
        var view = await chargeResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var paymentIntentId = view.GetProperty("paymentIntentId").GetGuid();

        // The Mongo read side is eventually consistent (outbox -> RabbitMQ -> projection consumer)
        // - poll with a generous timeout rather than asserting on the very next instruction.
        HttpResponseMessage? getResponse = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            getResponse = await client.GetAsync($"/v1/payments/{paymentIntentId}");
            if (getResponse.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Equal(HttpStatusCode.OK, getResponse!.StatusCode);
    }
}
