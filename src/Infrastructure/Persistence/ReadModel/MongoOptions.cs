namespace KartPaymentService.Infrastructure.Persistence.ReadModel;

/// <summary>Binds the "Mongo" configuration section. `ConnectionString` is a plain `mongodb://` URI - pointing it at a `mongodb://mongos-router...` sharded-cluster connection string in production requires no code change (docker-compose runs single-node Mongo for local dev, same convention as every sibling service).</summary>
public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    public string Database { get; set; } = "kart_payment_read";
}
