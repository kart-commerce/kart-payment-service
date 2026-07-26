using KartPaymentService.IntegrationTests;
using Xunit;

namespace KartPaymentService.ContractTests;

/// <summary>Verifies PAY-5 against contracts/api-contract.yaml's `POST /v1/payments/{id}/refund` path.</summary>
public sealed class RefundPaymentContractTests : IClassFixture<PaymentApiFactory>
{
    private const string ContractPath = "/v1/payments/{id}/refund";

    public RefundPaymentContractTests(PaymentApiFactory factory)
    {
        _ = factory; // fixture only needed to share the container lifecycle across the assembly
    }

    [Fact]
    public void Contract_DefinesRefundPath_WithIdempotencyKeyHeaderAnd202Response()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];
        Assert.True(paths.ContainsKey(ContractPath), $"api-contract.yaml no longer defines {ContractPath}");

        var postOp = (Dictionary<object, object>)((Dictionary<object, object>)paths[ContractPath])["post"];
        Assert.Equal("refundPayment", postOp["operationId"]);

        var parameters = (List<object>)postOp["parameters"];
        Assert.Contains(parameters.Cast<Dictionary<object, object>>(), p =>
            (string)p["name"] == "Idempotency-Key" && string.Equals(p["required"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

        var responses = (Dictionary<object, object>)postOp["responses"];
        Assert.True(responses.ContainsKey("202"));
        Assert.True(responses.ContainsKey("409"));
        Assert.True(responses.ContainsKey("404"));
    }

    [Fact]
    public void Contract_DefinesWebhookPath_WithGatewaySignatureSecurity()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];
        const string webhookPath = "/v1/payments/webhooks/{gateway}";
        Assert.True(paths.ContainsKey(webhookPath), $"api-contract.yaml no longer defines {webhookPath}");

        var postOp = (Dictionary<object, object>)((Dictionary<object, object>)paths[webhookPath])["post"];
        Assert.Equal("ingestGatewayWebhook", postOp["operationId"]);

        var security = (List<object>)postOp["security"];
        var securityKeys = security.Cast<Dictionary<object, object>>().SelectMany(s => s.Keys).Cast<string>();
        Assert.Contains("gatewaySignature", securityKeys);
    }
}
