using KartPaymentService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace KartPaymentService.IntegrationTests;

/// <summary>
/// Real Postgres + Mongo + RabbitMQ via Testcontainers - end-to-end coverage of the actual
/// unique-constraint/row-lock guarantees (EF Core InMemory doesn't enforce real DB constraints,
/// so the double-charge/over-refund race protections can only be genuinely proven against a real
/// Postgres). Shared across a test class via <see cref="IClassFixture{TFixture}"/>; migrations are
/// applied once in <see cref="InitializeAsync"/>.
/// </summary>
public sealed class PaymentApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("kart_payment_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder()
        .WithImage("mongo:7.0")
        .Build();

    // RabbitMQ's default "guest" user is restricted to loopback-only connections - a container
    // port mapped out to the host does not count as loopback from the broker's perspective, so a
    // dedicated non-guest user is required for the test process to authenticate at all.
    private const string RabbitMqUser = "test";
    private const string RabbitMqPassword = "test";

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management-alpine")
        .WithUsername(RabbitMqUser)
        .WithPassword(RabbitMqPassword)
        .Build();

    public const string SimulatedGatewaySigningSecret = "test-simulated-gateway-signing-secret";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mongo.StartAsync(), _rabbitMq.StartAsync());

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaymentDatabase"] = _postgres.GetConnectionString(),
                ["Mongo:ConnectionString"] = _mongo.GetConnectionString(),
                ["Mongo:Database"] = "kart_payment_read_test",
                ["RabbitMq:HostName"] = _rabbitMq.Hostname,
                ["RabbitMq:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:UserName"] = RabbitMqUser,
                ["RabbitMq:Password"] = RabbitMqPassword,
                ["Gateway:SigningSecrets:simulated"] = SimulatedGatewaySigningSecret,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _mongo.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await base.DisposeAsync();
    }
}
