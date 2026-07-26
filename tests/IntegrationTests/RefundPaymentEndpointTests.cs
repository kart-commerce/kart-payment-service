using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KartPaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KartPaymentService.IntegrationTests;

/// <summary>PAY-5 end-to-end, against real Postgres - proves the row-locked captured-amount-ceiling guard actually holds under real concurrency, not just the single-threaded handler logic UnitTests cover.</summary>
public sealed class RefundPaymentEndpointTests : IClassFixture<PaymentApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentApiFactory _factory;

    public RefundPaymentEndpointTests(PaymentApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient(string? roles = null)
    {
        var client = _factory.CreateClient();
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        }

        return client;
    }

    private async Task<Guid> ChargeAndGetIntentIdAsync(decimal amount)
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/charge")
        {
            Content = JsonContent.Create(new { orderId = $"order-{Guid.NewGuid():N}", amount = new { amount, currency = "USD" }, gatewayToken = "tok_good" }),
        };
        request.Headers.Add("Idempotency-Key", $"charge-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);
        var view = await response.Content.ReadFromJsonAsync<PaymentIntentViewResponse>(JsonOptions);
        return view!.PaymentIntentId;
    }

    private static HttpRequestMessage RefundRequest(Guid paymentIntentId, decimal amount, string idempotencyKey) => new(HttpMethod.Post, $"/v1/payments/{paymentIntentId}/refund")
    {
        Content = JsonContent.Create(new { amount = new { amount, currency = "USD" } }),
        Headers = { { "Idempotency-Key", idempotencyKey } },
    };

    [Fact]
    public async Task Refund_WithinCapturedAmount_Returns202()
    {
        var intentId = await ChargeAndGetIntentIdAsync(100m);
        var client = CreateClient();

        var response = await client.SendAsync(RefundRequest(intentId, 40m, $"refund-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Refund_ExceedingCapturedAmount_Returns409()
    {
        var intentId = await ChargeAndGetIntentIdAsync(100m);
        var client = CreateClient();

        var response = await client.SendAsync(RefundRequest(intentId, 150m, $"refund-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Refund_SupportAgentOverCap_Returns409()
    {
        var intentId = await ChargeAndGetIntentIdAsync(1000m);
        var client = CreateClient(roles: "support_agent");

        var response = await client.SendAsync(RefundRequest(intentId, 501m, $"refund-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Refund_ConcurrentRequestsExceedingCombinedCeiling_NeverAllowTotalRefundedToExceedCapturedAmount()
    {
        var intentId = await ChargeAndGetIntentIdAsync(100m);

        // Six concurrent refund requests of 30 each against a 100 ceiling - only three can ever
        // legitimately succeed (90 <= 100 < 120); the row lock (GetByIdForUpdateAsync) must
        // serialize these so the ceiling is never violated, regardless of which three win.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => CreateClient().SendAsync(RefundRequest(intentId, 30m, $"refund-{Guid.NewGuid():N}"))));

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        accepted.Should().BeLessOrEqualTo(3, "3 * 30 = 90 is the most that can fit under a 100 ceiling without violating it");
        (accepted + rejected).Should().Be(6);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var refundedTotal = await dbContext.Database.SqlQuery<decimal>(
                $"SELECT COALESCE(SUM(amount), 0) AS \"Value\" FROM refunds WHERE payment_intent_id = {intentId} AND status <> 'failed'")
            .SingleAsync();

        refundedTotal.Should().BeLessOrEqualTo(100m, "the captured-amount ceiling must never be violated no matter how many requests raced");
    }

    private sealed record PaymentIntentViewResponse(Guid PaymentIntentId, string OrderId, string Status, string? TxnId);
}
