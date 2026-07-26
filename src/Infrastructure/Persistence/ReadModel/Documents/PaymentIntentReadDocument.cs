using MongoDB.Bson.Serialization.Attributes;

namespace KartPaymentService.Infrastructure.Persistence.ReadModel.Documents;

/// <summary>
/// CQRS read-side, denormalized copy of the write-side `payment_intents` row (+ its `refunds`
/// children embedded) - `_id = paymentIntentId`. Kept in sync from PostgreSQL via the outbox ->
/// RabbitMQ -> read-model-projection pipeline (Infrastructure/Messaging/
/// ReadModelProjectionConsumerHostedService), never written by any request handler directly. This
/// collection is what `GetPaymentIntent` (PAY-4) actually reads - the user's explicit CQRS
/// requirement, documented as a deviation from database-design.md in contracts/README.md.
/// </summary>
public sealed class PaymentIntentReadDocument
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = string.Empty;

    [BsonElement("capturedAmount")]
    public decimal CapturedAmount { get; set; }

    [BsonElement("currency")]
    public string Currency { get; set; } = string.Empty;

    [BsonElement("txnId")]
    public string? TxnId { get; set; }

    [BsonElement("totalRefunded")]
    public decimal TotalRefunded { get; set; }

    [BsonElement("disputed")]
    public bool Disputed { get; set; }

    [BsonElement("refunds")]
    public List<RefundReadDocument> Refunds { get; set; } = new();

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
