using System.Text.Json;

namespace FreedomBlaze.Services.Webhooks;

/// <summary>The events the gateway posts to merchants.</summary>
public static class WebhookEventTypes
{
    /// <summary>The node proved the invoice paid (it showed the preimage). Final.</summary>
    public const string InvoicePaid = "invoice.paid";

    /// <summary>The invoice expired unpaid. Final: an expired Lightning invoice cannot be paid.</summary>
    public const string InvoiceExpired = "invoice.expired";

    /// <summary>Sent by hand from the management API to check an endpoint.</summary>
    public const string Test = "webhook.test";
}

/// <summary>The JSON body of a webhook: <c>{ id, type, timestamp, data }</c>, camel-cased like the API.</summary>
public static class WebhookEvent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string NewId() => "evt_" + Guid.NewGuid().ToString("N");

    public static string Serialize(string id, string type, DateTimeOffset timestamp, object data) =>
        JsonSerializer.Serialize(new { id, type, timestamp, data }, Json);
}
