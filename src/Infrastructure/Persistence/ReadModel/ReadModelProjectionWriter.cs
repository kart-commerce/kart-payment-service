using KartPaymentService.Infrastructure.Persistence.ReadModel.Documents;
using MongoDB.Driver;

namespace KartPaymentService.Infrastructure.Persistence.ReadModel;

/// <summary>
/// The write path for this service's CQRS read side - called exclusively by
/// <see cref="Messaging.ReadModelProjectionConsumerHostedService"/>, never by a request handler.
/// Every method is an idempotent upsert/set so at-least-once delivery of the same event never
/// corrupts the projection.
/// </summary>
public sealed class ReadModelProjectionWriter
{
    private readonly PaymentReadDbContext _context;

    public ReadModelProjectionWriter(PaymentReadDbContext context)
    {
        _context = context;
    }

    /// <summary>`PaymentCompleted` - the first event for this intent; seeds (or idempotently re-sets) the full read document.</summary>
    public Task UpsertOnCompletedAsync(Guid paymentIntentId, string orderId, string txnId, decimal capturedAmount, string currency, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var update = Builders<PaymentIntentReadDocument>.Update
            .Set(d => d.OrderId, orderId)
            .Set(d => d.Status, "completed")
            .Set(d => d.CapturedAmount, capturedAmount)
            .Set(d => d.Currency, currency)
            .Set(d => d.TxnId, txnId)
            .SetOnInsert(d => d.TotalRefunded, 0m)
            .SetOnInsert(d => d.Disputed, false)
            .SetOnInsert(d => d.Refunds, new List<RefundReadDocument>())
            .SetOnInsert(d => d.CreatedAt, updatedAt)
            .Set(d => d.UpdatedAt, updatedAt);

        return _context.PaymentIntents.UpdateOneAsync(d => d.Id == paymentIntentId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>`PaymentFailed` - may also be the first (and only) event for an intent that never completes.</summary>
    public Task UpsertOnFailedAsync(Guid paymentIntentId, string orderId, decimal capturedAmount, string currency, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var update = Builders<PaymentIntentReadDocument>.Update
            .Set(d => d.OrderId, orderId)
            .Set(d => d.Status, "failed")
            .Set(d => d.CapturedAmount, capturedAmount)
            .Set(d => d.Currency, currency)
            .SetOnInsert(d => d.TotalRefunded, 0m)
            .SetOnInsert(d => d.Disputed, false)
            .SetOnInsert(d => d.Refunds, new List<RefundReadDocument>())
            .SetOnInsert(d => d.CreatedAt, updatedAt)
            .Set(d => d.UpdatedAt, updatedAt);

        return _context.PaymentIntents.UpdateOneAsync(d => d.Id == paymentIntentId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>`RefundIssued` - appends the refund and bumps the running `totalRefunded` total.</summary>
    public Task AppendRefundAsync(Guid paymentIntentId, Guid refundId, decimal amount, DateTime requestedAt, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var refundDocument = new RefundReadDocument { RefundId = refundId, Amount = amount, Status = "succeeded", RequestedAt = requestedAt };
        var update = Builders<PaymentIntentReadDocument>.Update
            .Inc(d => d.TotalRefunded, amount)
            .Push(d => d.Refunds, refundDocument)
            .Set(d => d.UpdatedAt, updatedAt);

        return _context.PaymentIntents.UpdateOneAsync(
            d => d.Id == paymentIntentId && !d.Refunds.Any(r => r.RefundId == refundId),
            update,
            cancellationToken: cancellationToken);
    }

    /// <summary>`ChargebackReceived` - flips the disputed flag.</summary>
    public Task MarkDisputedAsync(Guid paymentIntentId, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var update = Builders<PaymentIntentReadDocument>.Update
            .Set(d => d.Status, "disputed")
            .Set(d => d.Disputed, true)
            .Set(d => d.UpdatedAt, updatedAt);

        return _context.PaymentIntents.UpdateOneAsync(d => d.Id == paymentIntentId, update, cancellationToken: cancellationToken);
    }
}
