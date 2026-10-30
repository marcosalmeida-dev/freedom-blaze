using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Services.Webhooks;

/// <summary>Queues events for a merchant's webhook endpoints.</summary>
public interface IWebhookOutbox
{
    /// <summary>
    /// Adds a delivery of one event to each of the merchant's active endpoints to <paramref name="db"/>,
    /// without saving: the caller saves it with the change the event reports, in one transaction. Returns
    /// how many deliveries were queued.
    /// </summary>
    Task<int> EnqueueAsync(FreedomBlazeDbContext db, int merchantId, string eventType, object data, long? invoiceId,
        CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IWebhookOutbox"/>. The dispatcher picks the deliveries up on its next round.</summary>
public sealed class WebhookOutbox(TimeProvider timeProvider) : IWebhookOutbox
{
    public async Task<int> EnqueueAsync(FreedomBlazeDbContext db, int merchantId, string eventType, object data, long? invoiceId,
        CancellationToken cancellationToken)
    {
        var endpoints = await db.WebhookEndpoints
            .Where(e => e.MerchantId == merchantId && e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
            return 0;

        var now = timeProvider.GetUtcNow();
        var eventId = WebhookEvent.NewId();
        var payload = WebhookEvent.Serialize(eventId, eventType, now, data);
        foreach (var endpointId in endpoints)
        {
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                MerchantId = merchantId,
                EndpointId = endpointId,
                EventId = eventId,
                EventType = eventType,
                InvoiceId = invoiceId,
                Payload = payload,
                State = WebhookDeliveryState.Pending,
                NextAttemptAtUtc = now,
                CreatedAtUtc = now,
            });
        }

        return endpoints.Count;
    }
}
