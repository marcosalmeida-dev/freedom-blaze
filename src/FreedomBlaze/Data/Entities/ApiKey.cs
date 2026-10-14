namespace FreedomBlaze.Data.Entities;

/// <summary>
/// A credential issued to an external client so it can call the public conversion API and issue
/// fiat-priced Lightning invoices (which can only receive funds, never send them). The secret
/// itself is never stored — only a SHA-256 hash (<see cref="KeyHash"/>) is persisted, exactly as a
/// password would be. The plaintext key is shown to the caller once, at creation time, and cannot be
/// recovered afterwards. A short, non-secret <see cref="Prefix"/> is kept to make keys identifiable
/// in logs and management UIs without exposing the secret.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }

    /// <summary>A human-friendly label for the client this key was issued to (e.g. "Acme checkout").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 hash of the plaintext secret. The lookup column for authentication.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Non-secret leading fragment of the key (scheme + first characters), e.g. <c>fb_live_Ab12cd</c>.
    /// Safe to display; lets an operator recognise a key without revealing it.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>When false the key is disabled and rejected even before expiry/revocation checks.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// A master key administers the gateway (keys, merchants, the node) and holds every scope. Ordinary
    /// keys are limited to their <see cref="Scopes"/>.
    /// </summary>
    public bool IsMaster { get; set; }

    /// <summary>
    /// The merchant the key acts for. Invoice scopes require one; master keys never have one, since they
    /// administer the gateway rather than act for a business.
    /// </summary>
    public int? MerchantId { get; set; }

    /// <summary>What the key may do. Master keys hold every scope regardless of this value.</summary>
    public ApiKeyScopes Scopes { get; set; }

    /// <summary>
    /// Optional per-key request budget per minute. When null the global default policy applies.
    /// </summary>
    public int? RateLimitPerMinute { get; set; }

    /// <summary>When the key was created (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Optional hard expiry (UTC). After this instant the key is rejected.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>Last time the key authenticated a request (UTC). Updated opportunistically.</summary>
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    /// <summary>When set, the key has been revoked at this instant (UTC) and is permanently rejected.</summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
