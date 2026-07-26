using System.Text;
using System.Text.Json;
using KartPaymentService.Application.Common;
using KartPaymentService.Application.Features.ChargePayment;
using KartPaymentService.Infrastructure.Security;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KartPaymentService.Infrastructure.Messaging;

/// <summary>
/// PAY-3's primary trigger (architecture.md's Sync/Async Resolution): consumes
/// `payment.order-events.queue` (bound to Order's own `order.exchange` / `order.order.created`)
/// and dispatches to the same <see cref="ChargePaymentCommand"/> code path
/// `POST /v1/payments/charge` uses. Derives a deterministic `Idempotency-Key` from
/// `(orderId, "charge")` since there is no inbound header on this async path - the same
/// `(idempotencyKey, endpoint)`-scoped mechanism applies uniformly regardless of entry point.
///
/// `OrderCreated`'s BRD-stated payload (`orderId, userId, items, total`) has no field for a
/// tokenized payment method - an unavoidable additive extension (`gatewayToken`) is required for
/// this async charge trigger to be implementable at all; documented in contracts/README.md.
/// </summary>
public sealed class OrderCreatedConsumerHostedService : BackgroundService
{
    private const string QueueName = "payment.order-events.queue";
    private const string RetryCountHeader = "x-payment-order-events-retry-count";

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionFactory _connectionFactory;
    private readonly MessageBusManifest _manifest;
    private readonly ILogger<OrderCreatedConsumerHostedService> _logger;

    public OrderCreatedConsumerHostedService(
        IServiceScopeFactory scopeFactory,
        IConnectionFactory connectionFactory,
        MessageBusManifest manifest,
        ILogger<OrderCreatedConsumerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionFactory = connectionFactory;
        _manifest = manifest;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = _connectionFactory.CreateConnection();
                using var channel = connection.CreateModel();
                RabbitMqTopologyProvisioner.Declare(channel, _manifest);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, deliverEventArgs) => await OnMessageReceivedAsync(channel, deliverEventArgs, stoppingToken);
                channel.BasicConsume(QueueName, autoAck: false, consumer);

                while (!stoppingToken.IsCancellationRequested && connection.IsOpen)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order-events consumer lost its RabbitMQ connection; reconnecting in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task OnMessageReceivedAsync(IModel channel, BasicDeliverEventArgs deliverEventArgs, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var json = Encoding.UTF8.GetString(deliverEventArgs.Body.Span);

            var payload = JsonSerializer.Deserialize<OrderCreatedEventPayload>(json, SerializerOptions)
                ?? throw new InvalidOperationException("OrderCreated payload deserialized to null.");

            var idempotencyKey = $"order:{payload.OrderId}:charge";
            var command = new ChargePaymentCommand(payload.OrderId, payload.Total, payload.Currency, payload.GatewayToken, idempotencyKey);

            using (CurrentPrincipalContext.SetScope(SystemPrincipals.OrderSagaPaymentConsumer))
            {
                var result = await sender.Send(command, stoppingToken);
                if (result.IsFailure)
                {
                    throw new InvalidOperationException($"ChargePayment failed: {result.Error.Code} - {result.Error.Message}");
                }
            }

            channel.BasicAck(deliverEventArgs.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            HandleFailure(channel, deliverEventArgs, ex);
        }
    }

    private void HandleFailure(IModel channel, BasicDeliverEventArgs deliverEventArgs, Exception ex)
    {
        var retryCount = RetryHeaders.GetRetryCount(deliverEventArgs.BasicProperties, RetryCountHeader);
        var tiers = _manifest.GetQueue(QueueName).RetryLadder?.Tiers ?? Array.Empty<RetryTierDefinition>();

        if (retryCount < tiers.Count)
        {
            var tier = tiers[retryCount];
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.Headers = new Dictionary<string, object> { [RetryCountHeader] = retryCount + 1 };

            channel.BasicPublish(exchange: string.Empty, routingKey: tier.Name, basicProperties: properties, body: deliverEventArgs.Body);
            channel.BasicAck(deliverEventArgs.DeliveryTag, multiple: false);

            _logger.LogWarning(ex, "Handling OrderCreated failed; routed to retry tier {Tier} (attempt {Attempt}).", tier.Name, retryCount + 1);
        }
        else
        {
            _logger.LogCritical(ex, "Handling OrderCreated failed after exhausting all retry tiers; dead-lettering.");
            channel.BasicNack(deliverEventArgs.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private sealed record OrderCreatedEventPayload(string OrderId, string GatewayToken, decimal Total, string Currency);
}
