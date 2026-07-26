using KartPaymentService.Infrastructure.Persistence.ReadModel.Documents;
using MongoDB.Driver;

namespace KartPaymentService.Infrastructure.Persistence.ReadModel;

/// <summary>
/// Typed accessor for this service's denormalized MongoDB read collection - the CQRS query side.
/// Deployed against a sharded MongoDB cluster in production (the user's explicit requirement);
/// nothing in this class assumes a single node.
/// </summary>
public sealed class PaymentReadDbContext
{
    public const string PaymentIntentsCollectionName = "payment_intents_read";

    public IMongoDatabase Database { get; }

    public PaymentReadDbContext(IMongoDatabase database)
    {
        Database = database;
    }

    public IMongoCollection<PaymentIntentReadDocument> PaymentIntents => Database.GetCollection<PaymentIntentReadDocument>(PaymentIntentsCollectionName);
}
