using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Models.Api;

/// <summary>Request to mint a new API key.</summary>
public sealed class CreateApiKeyRequest
{
    /// <summary>Human-friendly label identifying the client the key is issued to.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional expiry. When omitted the key never expires.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>Optional per-minute request budget overriding the global default.</summary>
    [Range(1, 100_000)]
    public int? RateLimitPerMinute { get; set; }

    /// <summary>
    /// When true the new key can also manage other keys. Only a caller already holding a master key
    /// can mint another master key.
    /// </summary>
    public bool IsMaster { get; set; }
}

/// <summary>
/// Returned once, immediately after a key is created. <see cref="Key"/> is the only time the
/// plaintext secret is ever exposed — it is not stored and cannot be recovered later.
/// </summary>
public sealed class CreateApiKeyResponse
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The plaintext secret. Store it securely now; it cannot be retrieved again.</summary>
    public string Key { get; init; } = string.Empty;

    public string Prefix { get; init; } = string.Empty;
    public bool IsMaster { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

/// <summary>Non-secret view of an issued key for management/listing.</summary>
public sealed class ApiKeyInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Prefix { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool IsMaster { get; init; }
    public int? RateLimitPerMinute { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}
