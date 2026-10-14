namespace FreedomBlaze.Data.Entities;

/// <summary>
/// A business the gateway takes Lightning payments for. Its API keys, and every invoice those keys
/// issue, are scoped to it: a key never sees another merchant's invoices.
/// <para>
/// Every merchant still shares the operator's phoenixd node — per-merchant Lightning backends are a
/// later step — so for now a merchant should be one of the operator's own projects. Taking payments
/// for an outside business on a shared node would mean holding that business's money.
/// </para>
/// </summary>
public class Merchant
{
    public int Id { get; set; }

    /// <summary>Display name, e.g. "Artcana".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A deactivated merchant's keys stop authenticating; its invoices are kept.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When the merchant was created (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the merchant was deactivated (UTC); null while active.</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; set; }
}
