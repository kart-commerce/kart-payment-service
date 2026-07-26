using RabbitMQ.Client;

namespace KartPaymentService.Infrastructure.Messaging;

/// <summary>
/// Declares a <see cref="MessageBusManifest"/>'s entire topology - exchanges (own and external),
/// dead-letter queues, retry-tier queues, main queues and their bindings - against a live channel.
/// RabbitMQ's declare/bind operations are themselves idempotent, so this is safe to call on every
/// (re)connect, by every hosted service that shares the manifest, in any order.
/// </summary>
public static class RabbitMqTopologyProvisioner
{
    public static void Declare(IModel channel, MessageBusManifest manifest)
    {
        foreach (var exchange in manifest.Exchanges.Concat(manifest.ExternalExchanges))
        {
            channel.ExchangeDeclare(exchange.Name, exchange.Type, durable: exchange.Durable);
        }

        foreach (var dlq in manifest.DeadLetterQueues)
        {
            channel.QueueDeclare(dlq.Name, durable: true, exclusive: false, autoDelete: false);
            channel.QueueBind(dlq.Name, dlq.Exchange, dlq.RoutingKey);
        }

        foreach (var queue in manifest.Queues)
        {
            DeclareRetryLadder(channel, queue.RetryLadder);

            var arguments = queue.DeadLetter is null
                ? null
                : new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = queue.DeadLetter.Exchange,
                    ["x-dead-letter-routing-key"] = queue.DeadLetter.RoutingKey,
                };

            channel.QueueDeclare(queue.Name, durable: queue.Durable, exclusive: false, autoDelete: false, arguments: arguments);

            foreach (var binding in queue.Bindings)
            {
                channel.QueueBind(queue.Name, binding.Exchange, binding.RoutingKey);
            }
        }
    }

    private static void DeclareRetryLadder(IModel channel, RetryLadderDefinition? retryLadder)
    {
        if (retryLadder is null)
        {
            return;
        }

        foreach (var tier in retryLadder.Tiers)
        {
            var arguments = new Dictionary<string, object>
            {
                ["x-message-ttl"] = tier.TtlMs,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = retryLadder.RequeueTo,
            };
            channel.QueueDeclare(tier.Name, durable: true, exclusive: false, autoDelete: false, arguments: arguments);
        }
    }
}
