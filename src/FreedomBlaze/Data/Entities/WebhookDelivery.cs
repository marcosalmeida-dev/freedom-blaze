namespace FreedomBlaze.Data.Entities;

public enum WebhookDeliveryState
{
    /// <summary>Waiting for its next attempt.</summary>
    Pending = 0,

    /// <summary>The endpoint answered 2xx. Final.</summary>
    Delivered = 1,

    /// <summary>Every attempt failed, or the endpoint was disabled. Final until redelivered by hand.</summary>
    Failed = 2,
}

/// <summary>
/// One event on its way to one endpoint: the outbox the dispatcher sends from, and the log the operator reads.
/// It is written in the same transaction as the invoice change it reports, so no change goes unannounced.
/// </summary>
public class WebhookDelivery
{
    public long Id { get; set; }

    public int MerchantId { get; set; }

    public int EndpointId { get; set; }

    /// <summary>
    /// The event's id, sent as <c>webhook-id</c>. Every delivery of one event to one endpoint shares it,
    /// redeliveries included, so the receiver can recognise a repeat.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>e.g. <c>invoice.paid</c>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The invoice the event is about, when it is about one.</summary>
    public long? InvoiceId { get; set; }

    /// <summary>The exact JSON body that is signed and posted.</summary>
    public string Payload { get; set; } = string.Empty;

    public WebhookDeliveryState State { get; set; }

    /// <summary>Attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due; null once final.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// Set while one dispatcher is sending it, so another instance does not send it too. A claim that is
    /// not released in time (the instance died) simply expires.
    /// </summary>
    public DateTimeOffset? ClaimedUntilUtc { get; set; }

    public DateTimeOffset? LastAttemptAtUtc { get; set; }

    /// <summary>The HTTP status of the last attempt; null when it got no answer.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>Why the last attempt failed.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? DeliveredAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
