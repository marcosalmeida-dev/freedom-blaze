namespace FreedomBlaze.Data.Entities;

/// <summary>
/// Where the gateway posts a merchant's events (<c>invoice.paid</c>, <c>invoice.expired</c>), signed with the
/// endpoint's own secret. An endpoint that is removed is only disabled: its delivery log is kept.
/// </summary>
public class WebhookEndpoint
{
    public int Id { get; set; }

    public int MerchantId { get; set; }

    /// <summary>The HTTPS URL events are posted to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>A note for the operator, e.g. "Artcana checkout".</summary>
    public string? Description { get; set; }

    /// <summary>The <c>whsec_…</c> signing secret, encrypted by the credential protector.</summary>
    public string ProtectedSecret { get; set; } = string.Empty;

    /// <summary>Disabled endpoints receive nothing; deliveries still pending for them fail.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? DisabledAtUtc { get; set; }
}
