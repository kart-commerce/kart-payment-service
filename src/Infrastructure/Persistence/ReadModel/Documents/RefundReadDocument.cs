using MongoDB.Bson.Serialization.Attributes;

namespace KartPaymentService.Infrastructure.Persistence.ReadModel.Documents;

/// <summary>Embedded (not a separate collection) - avoids a join for the common "get payment + its refunds" read.</summary>
public sealed class RefundReadDocument
{
    [BsonElement("refundId")]
    public Guid RefundId { get; set; }

    [BsonElement("amount")]
    public decimal Amount { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = string.Empty;

    [BsonElement("requestedAt")]
    public DateTime RequestedAt { get; set; }
}
