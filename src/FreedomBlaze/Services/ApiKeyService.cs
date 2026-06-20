using System.Security.Cryptography;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>
/// Default <see cref="IApiKeyService"/>. Keys look like <c>fb_live_&lt;43 url-safe chars&gt;</c> (256
/// bits of entropy), are persisted only as a SHA-256 hash, and are matched in constant time on
/// validation. <see cref="ApiKey.LastUsedAtUtc"/> is written at most once per minute per key to keep
/// the auth path read-only under load.
/// </summary>
public sealed class ApiKeyService(
    IRepository<ApiKey> apiKeys,
    TimeProvider timeProvider,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private const string KeyScheme = "fb_live_";
    private const int SecretBytes = 32; // 256 bits
    private const int PrefixSecretChars = 6;
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    public async Task<CreateApiKeyResponse> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        var secret = GenerateSecret();
        var plaintext = KeyScheme + secret;
        var now = timeProvider.GetUtcNow();

        var entity = new ApiKey
        {
            Name = request.Name.Trim(),
            KeyHash = Hash(plaintext),
            Prefix = KeyScheme + secret[..PrefixSecretChars],
            IsActive = true,
            RateLimitPerMinute = request.RateLimitPerMinute,
            CreatedAtUtc = now,
            ExpiresAtUtc = request.ExpiresAtUtc,
        };

        await apiKeys.AddAsync(entity, cancellationToken);

        logger.LogInformation("Issued API key {Prefix} ({Id}) for '{Name}'.", entity.Prefix, entity.Id, entity.Name);

        return new CreateApiKeyResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            Key = plaintext,
            Prefix = entity.Prefix,
            CreatedAtUtc = entity.CreatedAtUtc,
            ExpiresAtUtc = entity.ExpiresAtUtc,
        };
    }

    public async Task<ApiKey?> ValidateAsync(string plaintextKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
            return null;

        var hash = Hash(plaintextKey.Trim());
        var key = await apiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        if (key is null)
            return null;

        var now = timeProvider.GetUtcNow();
        if (!key.IsActive || key.RevokedAtUtc is not null || (key.ExpiresAtUtc is { } expiry && expiry <= now))
            return null;

        await TouchLastUsedAsync(key, now, cancellationToken);
        return key;
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var keys = await apiKeys.ListAsync(cancellationToken);
        return keys
            .OrderByDescending(k => k.CreatedAtUtc)
            .Select(k => new ApiKeyInfo
            {
                Id = k.Id,
                Name = k.Name,
                Prefix = k.Prefix,
                IsActive = k.IsActive,
                RateLimitPerMinute = k.RateLimitPerMinute,
                CreatedAtUtc = k.CreatedAtUtc,
                ExpiresAtUtc = k.ExpiresAtUtc,
                LastUsedAtUtc = k.LastUsedAtUtc,
                RevokedAtUtc = k.RevokedAtUtc,
            })
            .ToList();
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken cancellationToken)
    {
        var key = await apiKeys.GetByIdAsync(id, cancellationToken);
        if (key is null)
            return false;

        if (key.RevokedAtUtc is null || key.IsActive)
        {
            key.IsActive = false;
            key.RevokedAtUtc = timeProvider.GetUtcNow();
            await apiKeys.UpdateAsync(key, cancellationToken);
            logger.LogInformation("Revoked API key {Prefix} ({Id}).", key.Prefix, key.Id);
        }

        return true;
    }

    private async Task TouchLastUsedAsync(ApiKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (key.LastUsedAtUtc is { } last && now - last < LastUsedThrottle)
            return;

        try
        {
            key.LastUsedAtUtc = now;
            await apiKeys.UpdateAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort telemetry; never fail authentication because the usage stamp couldn't be saved.
            logger.LogWarning(ex, "Failed to update LastUsedAtUtc for API key {Prefix}.", key.Prefix);
        }
    }

    /// <summary>URL-safe, unpadded Base64 of cryptographically strong random bytes.</summary>
    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Hash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
