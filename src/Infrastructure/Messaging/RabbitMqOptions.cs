namespace KartPaymentService.Infrastructure.Messaging;

/// <summary>Binds the "RabbitMq" configuration section. Deliberately holds only connection info - everything topology-related lives in contracts/message-bus-manifest.json, not here.</summary>
public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";

    /// <summary>Defaults to RabbitMQ's standard AMQP port; overridden in tests, where Testcontainers maps the container's 5672 to a random host port.</summary>
    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string ManifestPath { get; set; } = "message-bus-manifest.json";
}
