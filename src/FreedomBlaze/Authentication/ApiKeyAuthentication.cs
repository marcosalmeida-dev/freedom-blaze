using FreedomBlaze.Data.Entities;
using Microsoft.AspNetCore.Authentication;

namespace FreedomBlaze.Authentication;

/// <summary>Shared constants for the API-key authentication scheme.</summary>
public static class ApiKeyDefaults
{
    /// <summary>The authentication scheme name used by <c>[Authorize]</c> and registration.</summary>
    public const string Scheme = "ApiKey";

    /// <summary>Primary header carrying the secret, e.g. <c>X-Api-Key: fb_live_…</c>.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>Custom claim type holding the database id of the authenticated key.</summary>
    public const string KeyIdClaim = "api_key_id";

    /// <summary>Claim type present (value "true") when the authenticated key is a master key.</summary>
    public const string MasterClaim = "api_key_master";

    /// <summary>Claim type holding the id of the merchant the key acts for; absent when it has none.</summary>
    public const string MerchantIdClaim = "merchant_id";

    /// <summary>Claim type, one per scope the key holds (see <see cref="ApiKeyScopeNames"/>).</summary>
    public const string ScopeClaim = "api_key_scope";

    /// <summary>Authorization policy that requires a master key. Guards the key-management endpoints.</summary>
    public const string MasterPolicy = "MasterKey";

    /// <summary>HttpContext.Items key under which the resolved per-key rate limit (int) is stashed.</summary>
    public const string RateLimitItemKey = "api_key_rate_limit";

    /// <summary>HttpContext.Items key under which the partition identifier (key id string) is stashed.</summary>
    public const string PartitionItemKey = "api_key_partition";
}

/// <summary>
/// Authorization policies that each require one scope. Master keys carry every scope claim, so they
/// satisfy all of them.
/// </summary>
public static class ApiKeyPolicies
{
    public const string Convert = "ApiKeyScope:" + ApiKeyScopeNames.Convert;
    public const string InvoicesWrite = "ApiKeyScope:" + ApiKeyScopeNames.InvoicesWrite;
    public const string InvoicesRead = "ApiKeyScope:" + ApiKeyScopeNames.InvoicesRead;

    /// <summary>Every scope policy with the scope it requires.</summary>
    public static IReadOnlyList<(string Policy, string Scope)> All { get; } =
    [
        (Convert, ApiKeyScopeNames.Convert),
        (InvoicesWrite, ApiKeyScopeNames.InvoicesWrite),
        (InvoicesRead, ApiKeyScopeNames.InvoicesRead),
    ];
}

/// <summary>Options for <see cref="ApiKeyAuthenticationHandler"/> (no extra settings today).</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;
