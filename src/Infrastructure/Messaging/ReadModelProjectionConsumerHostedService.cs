using System.Text;
using System.Text.Json;
using Kart.Shared.Messaging;
using Kart.Shared.Observability;
using KartPaymentService.Infrastructure.Persistence.ReadModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KartPaymentService.Infrastructure.Messaging;

/// <summary>
/// The CQRS sync mechanism the user's requirements explicitly call for: self-consumes every
/// `Payment*`/`RefundIssued`/`ChargebackReceived` event this service just published on its own
/// `payment.exchange` (`payment.read-model-projection.queue`, bound with wildcard routing keys)
/// and applies the equivalent change to the MongoDB denormalized read model via
/// <see cref="ReadModelProjectionWriter"/>. This is the only path that ever writes to the read
/// side - PostgreSQL (via the transactional outbox) remains the sole source of truth, and the read
/// model is always rebuildable by replaying the outbox/event log, never a second place business
/// logic writes to directly. Mirrors kart-offer-service's identically-shaped projection consumer.
/// </summary>
public sealed class ReadModelProjectionConsumerHostedService : BackgroundService
{
    private const string QueueName = "payment.read-model-projection.queue";
    private const string RetryCountHeader = "x-payment-read-model-projection-retry-count";
    private const string FlowName = "PaymentProcessingFraudCheck";

    /// <summary>Same event-type-based Flow split as OutboxRelayHostedService - PaymentCompleted/PaymentFailed are the customer-facing checkout events (business-flows.md flow #1), everything else belongs to flow #6's own dedicated Payment Processing &amp; Fraud Check flow.</summary>
    private const string ShoppingJourneyFlowName = "NormalShoppingPurchaseJourney";
    private static readonly HashSet<string> ShoppingJourneyEventTypes = ["PaymentCompleted", "PaymentFailed"];

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionFactory _connectionFactory;
    private readonly MessageBusManifest _manifest;
    private readonly ILogger<ReadModelProjectionConsumerHostedService> _logger;

    public ReadModelProjectionConsumerHostedService(
        IServiceScopeFactory scopeFactory,
        IConnectionFactory connectionFactory,
        MessageBusManifest manifest,
        ILogger<ReadModelProjectionConsumerHostedService> logger)
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
                _logger.LogError(ex, "Read-model-projection consumer lost its RabbitMQ connection; reconnecting in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task OnMessageReceivedAsync(IModel channel, BasicDeliverEventArgs deliverEventArgs, CancellationToken stoppingToken)
    {
        var eventType = _manifest.EventTypeForRoutingKey(deliverEventArgs.RoutingKey);

        // Previously missing entirely - this self-consumption hop (this service's own
        // OutboxRelayHostedService publish, immediately consumed back by this same service) never
        // continued the W3C trace stored on the outbox row/stamped on the AMQP headers, so every
        // read-model-projection log was a disconnected root trace instead of the same TraceId the
        // original charge/refund/webhook request started with. Mirrors OrderCreatedConsumerHostedService's
        // identical fix for the cross-service hop.
        using var activity = RabbitMqTraceContext.StartConsumeActivity(QueueName, deliverEventArgs.BasicProperties);
        using var flowScope = KartFlowContext.Push(ShoppingJourneyEventTypes.Contains(eventType) ? ShoppingJourneyFlowName : FlowName);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<ReadModelProjectionWriter>();
            var json = Encoding.UTF8.GetString(deliverEventArgs.Body.Span);

            _logger.LogInformation(
                "Stage {Stage}: {EventType} event consumed from {Queue} for read-model projection",
                "ReadModelProjectionEventConsumed",
                eventType,
                QueueName);

            await ProjectAsync(writer, eventType, json, stoppingToken);

            channel.BasicAck(deliverEventArgs.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            HandleFailure(channel, deliverEventArgs, ex);
        }
    }

    private async Task ProjectAsync(ReadModelProjectionWriter writer, string eventType, string json, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        switch (eventType)
        {
            case "PaymentCompleted":
            {
                var payload = Deserialize<PaymentCompletedPayload>(json);
                _logger.LogInformation("Stage {Stage}: read-model write started for payment intent {PaymentIntentId}, event {EventType}", "ReadModelWriteStarted", payload.PaymentIntentId, eventType);
                await writer.UpsertOnCompletedAsync(payload.PaymentIntentId, payload.OrderId, payload.TxnId, payload.CapturedAmount, payload.Currency, now, cancellationToken);
                _logger.LogInformation("Stage {Stage}: read-model persisted for payment intent {PaymentIntentId}, status {Status}", "ReadModelPersisted", payload.PaymentIntentId, "completed");
                break;
            }
            case "PaymentFailed":
            {
                var payload = Deserialize<PaymentFailedPayload>(json);
                _logger.LogInformation("Stage {Stage}: read-model write started for payment intent {PaymentIntentId}, event {EventType}", "ReadModelWriteStarted", payload.PaymentIntentId, eventType);
                await writer.UpsertOnFailedAsync(payload.PaymentIntentId, payload.OrderId, payload.CapturedAmount, payload.Currency, now, cancellationToken);
                _logger.LogInformation("Stage {Stage}: read-model persisted for payment intent {PaymentIntentId}, status {Status}", "ReadModelPersisted", payload.PaymentIntentId, "failed");
                break;
            }
            case "RefundIssued":
            {
                var payload = Deserialize<RefundIssuedPayload>(json);
                _logger.LogInformation("Stage {Stage}: read-model write started for payment intent {PaymentIntentId}, refund {RefundId}, event {EventType}", "ReadModelWriteStarted", payload.PaymentIntentId, payload.RefundId, eventType);
                await writer.AppendRefundAsync(payload.PaymentIntentId, payload.RefundId, payload.Amount, now, now, cancellationToken);
                _logger.LogInformation("Stage {Stage}: read-model persisted for payment intent {PaymentIntentId}, refund {RefundId} appended", "ReadModelPersisted", payload.PaymentIntentId, payload.RefundId);
                break;
            }
            case "ChargebackReceived":
            {
                var payload = Deserialize<ChargebackReceivedPayload>(json);
                _logger.LogInformation("Stage {Stage}: read-model write started for payment intent {PaymentIntentId}, event {EventType}", "ReadModelWriteStarted", payload.PaymentIntentId, eventType);
                await writer.MarkDisputedAsync(payload.PaymentIntentId, now, cancellationToken);
                _logger.LogInformation("Stage {Stage}: read-model persisted for payment intent {PaymentIntentId}, status {Status}", "ReadModelPersisted", payload.PaymentIntentId, "disputed");
                break;
            }
            default:
                throw new InvalidOperationException($"Read-model-projection consumer has no handling for event type '{eventType}'.");
        }
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? throw new InvalidOperationException($"{typeof(T).Name} payload deserialized to null.");

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

            _logger.LogWarning(ex, "Read-model projection failed for routing key {RoutingKey}; routed to retry tier {Tier} (attempt {Attempt}).", deliverEventArgs.RoutingKey, tier.Name, retryCount + 1);
        }
        else
        {
            _logger.LogCritical(ex, "Read-model projection failed after exhausting all retry tiers for routing key {RoutingKey}; dead-lettering. The read model will lag until this is replayed from the DLQ.", deliverEventArgs.RoutingKey);
            channel.BasicNack(deliverEventArgs.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private sealed record PaymentCompletedPayload(Guid PaymentIntentId, string OrderId, string TxnId, decimal CapturedAmount, string Currency);
    private sealed record PaymentFailedPayload(Guid PaymentIntentId, string OrderId, string Reason, decimal CapturedAmount, string Currency);
    private sealed record RefundIssuedPayload(Guid PaymentIntentId, Guid RefundId, string OrderId, decimal Amount, string Currency);
    private sealed record ChargebackReceivedPayload(Guid PaymentIntentId, string OrderId, string ChargebackId, decimal Amount, string Currency, string Reason);
}
