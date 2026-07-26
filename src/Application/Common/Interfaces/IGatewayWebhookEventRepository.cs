using KartPaymentService.Domain.Webhooks;

namespace KartPaymentService.Application.Common.Interfaces;

public interface IGatewayWebhookEventRepository
{
    Task<GatewayWebhookEvent?> GetAsync(string gatewayEventId, CancellationToken cancellationToken);

    Task AddAsync(GatewayWebhookEvent webhookEvent, CancellationToken cancellationToken);
}
