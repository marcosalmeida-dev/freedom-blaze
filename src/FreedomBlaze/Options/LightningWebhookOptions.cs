namespace FreedomBlaze.Options;

/// <summary>How the gateway posts merchants' webhooks, bound from the "LightningWebhooks" section.</summary>
public sealed class LightningWebhookOptions
{
    public const string Section = "LightningWebhooks";

    /// <summary>Accept <c>http://</c> endpoint URLs. Local development only.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>How long an endpoint has to answer one delivery.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// The background worker that confirms pending invoices with their nodes and sends due webhooks, bound
/// from the "LightningWorker" section.
/// </summary>
public sealed class LightningWorkerOptions
{
    public const string Section = "LightningWorker";

    /// <summary>Turn the worker off, e.g. on a replica that should only serve requests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pause between rounds.</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>Invoices checked, and webhooks sent, per round at most.</summary>
    public int BatchSize { get; set; } = 50;
}
