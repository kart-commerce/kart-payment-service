using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KartPaymentService.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory `dotnet ef migrations add`/`database update` use to build
/// <see cref="PaymentDbContext"/> without spinning up the full Api host. Never used at runtime -
/// the app's own DI registration (Infrastructure/DependencyInjection.cs) takes over there.
/// </summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("PAYMENT_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=kart_payment;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<PaymentDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new PaymentDbContext(optionsBuilder.Options);
    }
}
