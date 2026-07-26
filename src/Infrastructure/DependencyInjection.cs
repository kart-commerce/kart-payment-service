using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Infrastructure.GatewayReconciliationJob;
using KartPaymentService.Infrastructure.Idempotency;
using KartPaymentService.Infrastructure.IdempotencyPartitionMaintenance;
using KartPaymentService.Infrastructure.Messaging;
using KartPaymentService.Infrastructure.PaymentGateway;
using KartPaymentService.Infrastructure.Persistence;
using KartPaymentService.Infrastructure.Persistence.ReadModel;
using KartPaymentService.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace KartPaymentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddWriteSidePersistence(services, configuration);
        AddReadSidePersistence(services, configuration);
        AddPaymentGateway(services);
        AddMessaging(services, configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentPrincipal, HttpCurrentPrincipal>();

        services.AddHostedService<IdempotencyPartitionMaintenanceHostedService>();
        services.AddHostedService<GatewayReconciliationJobHostedService>();

        return services;
    }

    /// <summary>PostgreSQL - the sole write-side source of truth for all three aggregates (database-design.md).</summary>
    private static void AddWriteSidePersistence(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("PaymentDatabase")));

        services.AddScoped<IPaymentIntentRepository, PaymentIntentRepository>();
        services.AddScoped<IIdempotencyRecordRepository, IdempotencyRecordRepository>();
        services.AddScoped<IGatewayWebhookEventRepository, GatewayWebhookEventRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IIdempotencyGuard, EfIdempotencyGuard>();
    }

    /// <summary>
    /// MongoDB (sharded in production) - the CQRS read side, per the user's explicit requirement
    /// (a deliberate deviation from database-design.md's own "no read model needed" call - see
    /// contracts/README.md). Denormalized, eventually-consistent projections kept in sync from
    /// PostgreSQL exclusively via <see cref="ReadModelProjectionConsumerHostedService"/>; never
    /// written to by a request handler.
    /// </summary>
    private static void AddReadSidePersistence(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MongoOptions>(configuration.GetSection("Mongo"));

        services.AddSingleton<IMongoClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
            var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
            // requirement-spec.md's P95<150ms/P99<400ms read-path SLA: fail fast into the global
            // exception handler during a shard/replica-set outage, not hang for the driver's 30s
            // default server-selection timeout.
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            return new MongoClient(settings);
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
            return new PaymentReadDbContext(sp.GetRequiredService<IMongoClient>().GetDatabase(options.Database));
        });
        services.AddHostedService<MongoIndexInitializerHostedService>();

        services.AddScoped<IPaymentIntentReadRepository, PaymentIntentReadRepository>();
        services.AddScoped<ReadModelProjectionWriter>();
    }

    /// <summary>
    /// PAY-1: gateway-agnostic adapter behind <see cref="IPaymentGatewayAdapter"/>. The simulated
    /// concrete implementation is registered as a singleton (its in-memory charge ledger is what
    /// lets PAY-10's reconciliation "ask the gateway again"); the resilience decorator (Decorator
    /// pattern) wraps it and is what Application code actually depends on.
    /// </summary>
    private static void AddPaymentGateway(IServiceCollection services)
    {
        services.AddSingleton<SimulatedPaymentGatewayAdapter>();
        services.AddSingleton<IPaymentGatewayAdapter>(sp =>
            new ResilientPaymentGatewayAdapter(
                sp.GetRequiredService<SimulatedPaymentGatewayAdapter>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientPaymentGatewayAdapter>>()));
    }

    /// <summary>
    /// contracts/message-bus-manifest.json is the single source of truth for this service's
    /// entire RabbitMQ topology - every exchange, queue, binding, dead-letter and retry-tier name.
    /// Nothing messaging-related is hardcoded in C#: the manifest is loaded once here and shared
    /// as a singleton; RabbitMqTopologyProvisioner scans it to declare the topology.
    /// </summary>
    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection("RabbitMq"));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            var manifestPath = Path.IsPathRooted(options.ManifestPath)
                ? options.ManifestPath
                : Path.Combine(AppContext.BaseDirectory, options.ManifestPath);
            return MessageBusManifestLoader.Load(manifestPath);
        });
        services.AddSingleton<IConnectionFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            return new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                DispatchConsumersAsync = true,
            };
        });

        services.AddHostedService<RabbitMqTopologyStartupHostedService>();
        services.AddHostedService<OutboxRelayHostedService>();
        services.AddHostedService<OrderCreatedConsumerHostedService>();
        services.AddHostedService<ReadModelProjectionConsumerHostedService>();
    }
}
