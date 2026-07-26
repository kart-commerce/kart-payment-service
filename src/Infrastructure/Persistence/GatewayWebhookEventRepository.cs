using KartPaymentService.Application.Common.Interfaces;
using KartPaymentService.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace KartPaymentService.Infrastructure.Persistence;

public sealed class GatewayWebhookEventRepository(PaymentDbContext dbContext) : IGatewayWebhookEventRepository
{
    public Task<GatewayWebhookEvent?> GetAsync(string gatewayEventId, CancellationToken cancellationToken) =>
        dbContext.GatewayWebhookEvents.FirstOrDefaultAsync(e => e.GatewayEventId == gatewayEventId, cancellationToken);

    public Task AddAsync(GatewayWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        dbContext.GatewayWebhookEvents.Add(webhookEvent);
        return Task.CompletedTask;
    }
}
