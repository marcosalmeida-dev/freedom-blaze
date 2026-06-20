using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>
/// Issues, validates and revokes the API keys that authenticate external clients to the public API.
/// Secrets are stored only as salted-free SHA-256 hashes (the key has full entropy, so a per-key salt
/// adds nothing); the plaintext is returned exactly once at creation.
/// </summary>
public interface IApiKeyService
{
    /// <summary>Creates a new key, persists its hash, and returns the plaintext secret once.</summary>
    Task<CreateApiKeyResponse> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a plaintext key to its active, non-expired, non-revoked record, or returns null when
    /// it is invalid. Updates <see cref="ApiKey.LastUsedAtUtc"/> opportunistically on success.
    /// </summary>
    Task<ApiKey?> ValidateAsync(string plaintextKey, CancellationToken cancellationToken);

    /// <summary>Lists all issued keys (non-secret view).</summary>
    Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Revokes a key by id. Returns false when no such key exists. Idempotent.</summary>
    Task<bool> RevokeAsync(int id, CancellationToken cancellationToken);
}
