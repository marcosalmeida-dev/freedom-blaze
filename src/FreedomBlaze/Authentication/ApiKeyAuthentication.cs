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

    /// <summary>HttpContext.Items key under which the resolved per-key rate limit (int) is stashed.</summary>
    public const string RateLimitItemKey = "api_key_rate_limit";

    /// <summary>HttpContext.Items key under which the partition identifier (key id string) is stashed.</summary>
    public const string PartitionItemKey = "api_key_partition";
}

/// <summary>Options for <see cref="ApiKeyAuthenticationHandler"/> (no extra settings today).</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;
