using System.Security.Cryptography;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>
/// Default <see cref="IApiKeyService"/>. Keys look like <c>fb_live_&lt;43 url-safe chars&gt;</c> (256
/// bits of entropy), are persisted only as a SHA-256 hash, and are matched in constant time on
/// validation. <see cref="ApiKey.LastUsedAtUtc"/> is written at most once per minute per key to keep
/// the auth path read-only under load. A key bound to a merchant stops authenticating while that
/// merchant is deactivated.
/// </summary>
public sealed class ApiKeyService(
    IRepository<ApiKey> apiKeys,
    IRepository<Merchant> merchants,
    TimeProvider timeProvider,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private const ApiKeyScopes InvoiceScopes = ApiKeyScopes.InvoicesWrite | ApiKeyScopes.InvoicesRead;

    private const string KeyScheme = "fb_live_";
    private const int SecretBytes = 32; // 256 bits
    private const int PrefixSecretChars = 6;
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    public async Task<CreateApiKeyResponse> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        var scopes = ResolveScopes(request);
        if (request.MerchantId is { } merchantId
            && !await merchants.AnyAsync(m => m.Id == merchantId && m.IsActive, cancellationToken))
            throw new RequestValidationException($"Merchant {merchantId} does not exist or is inactive.");

        var secret = GenerateSecret();
        var plaintext = KeyScheme + secret;
        var now = timeProvider.GetUtcNow();

        var entity = new ApiKey
        {
            Name = request.Name.Trim(),
            KeyHash = Hash(plaintext),
            Prefix = KeyScheme + secret[..PrefixSecretChars],
            IsActive = true,
            IsMaster = request.IsMaster,
            MerchantId = request.MerchantId,
            Scopes = scopes,
            RateLimitPerMinute = request.RateLimitPerMinute,
            CreatedAtUtc = now,
            ExpiresAtUtc = request.ExpiresAtUtc,
        };

        await apiKeys.AddAsync(entity, cancellationToken);

        logger.LogInformation("Issued API key {Prefix} ({Id}) for '{Name}' (merchant {MerchantId}, scopes {Scopes}).",
            entity.Prefix, entity.Id, entity.Name, entity.MerchantId, string.Join(' ', ApiKeyScopeNames.ToNames(scopes)));

        return new CreateApiKeyResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            Key = plaintext,
            Prefix = entity.Prefix,
            IsMaster = entity.IsMaster,
            MerchantId = entity.MerchantId,
            Scopes = ApiKeyScopeNames.ToNames(scopes),
            CreatedAtUtc = entity.CreatedAtUtc,
            ExpiresAtUtc = entity.ExpiresAtUtc,
        };
    }

    /// <summary>
    /// The scopes a new key gets. Master keys get everything and act for no merchant; invoice scopes need
    /// a merchant, because every invoice belongs to one.
    /// </summary>
    private static ApiKeyScopes ResolveScopes(CreateApiKeyRequest request)
    {
        if (request.IsMaster)
        {
            return request.MerchantId is null
                ? ApiKeyScopes.All
                : throw new RequestValidationException("A master key administers the gateway and cannot be bound to a merchant.");
        }

        var scopes = request.MerchantId is null ? ApiKeyScopes.Convert : ApiKeyScopes.All;
        if (request.Scopes is { } names)
        {
            if (!ApiKeyScopeNames.TryParse(names, out scopes, out var unknown))
                throw new RequestValidationException(
                    $"Unknown scope '{unknown}'. Valid scopes: {string.Join(", ", ApiKeyScopeNames.All)}.");
        }

        if (scopes == ApiKeyScopes.None)
            throw new RequestValidationException("A key needs at least one scope.");

        if ((scopes & InvoiceScopes) != 0 && request.MerchantId is null)
            throw new RequestValidationException("The invoice scopes need a merchantId: every invoice belongs to a merchant.");

        return scopes;
    }

    public async Task<CreateApiKeyResponse?> EnsureMasterKeyAsync(CancellationToken cancellationToken)
    {
        var hasMaster = await apiKeys.AnyAsync(
            k => k.IsMaster && k.IsActive && k.RevokedAtUtc == null,
            cancellationToken);

        if (hasMaster)
            return null;

        return await CreateAsync(
            new CreateApiKeyRequest { Name = "Master key (auto-generated)", IsMaster = true },
            cancellationToken);
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

        // A deactivated merchant's keys are refused outright rather than scope by scope.
        if (key.MerchantId is { } merchantId
            && !await merchants.AnyAsync(m => m.Id == merchantId && m.IsActive, cancellationToken))
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
                IsMaster = k.IsMaster,
                MerchantId = k.MerchantId,
                Scopes = ApiKeyScopeNames.ToNames(k.IsMaster ? ApiKeyScopes.All : k.Scopes),
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
