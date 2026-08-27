using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KartPaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KartPaymentService.IntegrationTests;

/// <summary>PAY-3 end-to-end, against real Postgres - proves the actual double-charge guard, not just the in-memory handler logic UnitTests already cover.</summary>
public sealed class ChargePaymentEndpointTests : IClassFixture<PaymentApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentApiFactory _factory;

    public ChargePaymentEndpointTests(PaymentApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private static HttpRequestMessage ChargeRequest(string orderId, decimal amount, string gatewayToken, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/charge")
        {
            Content = JsonContent.Create(new { orderId, amount = new { amount, currency = "USD" }, gatewayToken }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    [Fact]
    public async Task Charge_GoodToken_ReturnsCompleted()
    {
        var client = CreateClient();
        var orderId = $"order-{Guid.NewGuid():N}";

        var response = await client.SendAsync(ChargeRequest(orderId, 25m, "tok_good", $"key-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<PaymentIntentViewResponse>(JsonOptions);
        view!.Status.Should().Be("completed");
        view.TxnId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Charge_SameIdempotencyKeySameBody_Twice_ReturnsIdenticalResponse_NoSecondPaymentIntentRow()
    {
        var client = CreateClient();
        var orderId = $"order-{Guid.NewGuid():N}";
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        var first = await client.SendAsync(ChargeRequest(orderId, 15m, "tok_good", idempotencyKey));
        var second = await client.SendAsync(ChargeRequest(orderId, 15m, "tok_good", idempotencyKey));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstView = await first.Content.ReadFromJsonAsync<PaymentIntentViewResponse>(JsonOptions);
        var secondView = await second.Content.ReadFromJsonAsync<PaymentIntentViewResponse>(JsonOptions);
        secondView!.PaymentIntentId.Should().Be(firstView!.PaymentIntentId, "the replayed request must return the exact same stored result, never a second charge");

        await AssertExactlyOnePaymentIntentForOrderAsync(orderId);
    }

    [Fact]
    public async Task Charge_SameIdempotencyKeyDifferentBody_ReturnsConflict()
    {
        var client = CreateClient();
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        var first = await client.SendAsync(ChargeRequest($"order-{Guid.NewGuid():N}", 15m, "tok_good", idempotencyKey));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.SendAsync(ChargeRequest($"order-{Guid.NewGuid():N}", 999m, "tok_good", idempotencyKey));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Charge_ConcurrentRequestsWithTheSameIdempotencyKey_NeverProduceTwoPaymentIntents()
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        // 10 concurrent requests racing on the exact same Idempotency-Key - the real-world shape
        // of a client retrying after a timeout while the original request is still in flight.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => CreateClient().SendAsync(ChargeRequest(orderId, 20m, "tok_good", idempotencyKey))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK, "every racing request with an identical payload must be served as a safe replay, never an error");

        await AssertExactlyOnePaymentIntentForOrderAsync(orderId);
    }

    private async Task AssertExactlyOnePaymentIntentForOrderAsync(string orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var orderIdValue = new KartPaymentService.Domain.Payments.OrderId(orderId);
        var count = await dbContext.PaymentIntents.CountAsync(p => p.OrderId == orderIdValue);
        count.Should().Be(1, "the (idempotency_key, endpoint) and payment_intents.order_id unique constraints together must guarantee exactly one charge attempt");
    }

    private sealed record PaymentIntentViewResponse(Guid PaymentIntentId, string OrderId, string Status, string? TxnId);
}
