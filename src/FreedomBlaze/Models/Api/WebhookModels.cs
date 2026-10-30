using System.ComponentModel.DataAnnotations;
using FreedomBlaze.Data.Entities;

namespace FreedomBlaze.Models.Api;

/// <summary>Registers an HTTPS endpoint that receives the merchant's events.</summary>
public sealed class CreateWebhookEndpointRequest
{
    [Required, StringLength(2048)]
    public string Url { get; set; } = string.Empty;

    /// <summary>A note for the operator, e.g. "Artcana checkout".</summary>
    [StringLength(128)]
    public string? Description { get; set; }
}

/// <summary>Changes to an endpoint; omitted fields are left as they are.</summary>
public sealed class UpdateWebhookEndpointRequest
{
    [StringLength(2048)]
    public string? Url { get; set; }

    [StringLength(128)]
    public string? Description { get; set; }

    /// <summary>False stops deliveries to it (pending ones fail); true resumes them.</summary>
    public bool? IsActive { get; set; }
}

/// <summary>An endpoint as the management API shows it. The signing secret is never shown again.</summary>
public class WebhookEndpointInfo
{
    public int Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? DisabledAtUtc { get; init; }

    public static WebhookEndpointInfo From(WebhookEndpoint endpoint) => new()
    {
        Id = endpoint.Id,
        Url = endpoint.Url,
        Description = endpoint.Description,
        IsActive = endpoint.IsActive,
        CreatedAtUtc = endpoint.CreatedAtUtc,
        DisabledAtUtc = endpoint.DisabledAtUtc,
    };
}

/// <summary>
/// An endpoint with its signing secret, returned once: when it is created and when its secret is rotated.
/// Verify each delivery's <c>webhook-signature</c> with it (Standard Webhooks).
/// </summary>
public sealed class WebhookEndpointWithSecret : WebhookEndpointInfo
{
    /// <summary>The <c>whsec_…</c> signing secret. Store it now: it cannot be shown again.</summary>
    public string Secret { get; init; } = string.Empty;

    public static WebhookEndpointWithSecret From(WebhookEndpoint endpoint, string secret) => new()
    {
        Id = endpoint.Id,
        Url = endpoint.Url,
        Description = endpoint.Description,
        IsActive = endpoint.IsActive,
        CreatedAtUtc = endpoint.CreatedAtUtc,
        DisabledAtUtc = endpoint.DisabledAtUtc,
        Secret = secret,
    };
}

/// <summary>What happened when a test event was posted.</summary>
public sealed class WebhookTestResult
{
    public bool Delivered { get; init; }
    public int? StatusCode { get; init; }
    public int DurationMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>One delivery in the log.</summary>
public sealed class WebhookDeliveryInfo
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";

    public long Id { get; init; }
    public int EndpointId { get; init; }
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public long? InvoiceId { get; init; }

    /// <summary><c>pending</c>, <c>delivered</c> or <c>failed</c>.</summary>
    public string State { get; init; } = Pending;

    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? DeliveredAtUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }

    public static WebhookDeliveryInfo From(WebhookDelivery delivery) => new()
    {
        Id = delivery.Id,
        EndpointId = delivery.EndpointId,
        EventId = delivery.EventId,
        EventType = delivery.EventType,
        InvoiceId = delivery.InvoiceId,
        State = StateName(delivery.State),
        Attempts = delivery.Attempts,
        NextAttemptAtUtc = delivery.NextAttemptAtUtc,
        LastAttemptAtUtc = delivery.LastAttemptAtUtc,
        LastStatusCode = delivery.LastStatusCode,
        LastError = delivery.LastError,
        DeliveredAtUtc = delivery.DeliveredAtUtc,
        CreatedAtUtc = delivery.CreatedAtUtc,
    };

    public static string StateName(WebhookDeliveryState state) => state switch
    {
        WebhookDeliveryState.Delivered => Delivered,
        WebhookDeliveryState.Failed => Failed,
        _ => Pending,
    };

    public static bool TryParseState(string? name, out WebhookDeliveryState state)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case Pending:
                state = WebhookDeliveryState.Pending;
                return true;
            case Delivered:
                state = WebhookDeliveryState.Delivered;
                return true;
            case Failed:
                state = WebhookDeliveryState.Failed;
                return true;
            default:
                state = default;
                return false;
        }
    }
}

/// <summary>One page of a merchant's deliveries, newest first.</summary>
public sealed class WebhookDeliveryPage
{
    public IReadOnlyList<WebhookDeliveryInfo> Items { get; init; } = [];

    /// <summary>Pass as <c>before</c> to fetch the next (older) page; null on the last page.</summary>
    public long? NextCursor { get; init; }
}
