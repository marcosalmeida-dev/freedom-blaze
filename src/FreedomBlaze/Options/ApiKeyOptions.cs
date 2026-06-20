namespace FreedomBlaze.Options;

public class ApiKeyOptions
{
    public const string Section = "ApiKeys";

    /// <summary>
    /// Shared secret that guards the key-management endpoints (create/list/revoke). Issuing API keys
    /// is an administrative action, so it is protected separately from the issued keys themselves.
    /// When empty, the management endpoints are disabled (return 404), so a deployment that does not
    /// set this cannot have keys minted over HTTP.
    /// </summary>
    public string? AdminToken { get; set; }

    /// <summary>Default per-key request budget per minute when a key does not specify its own.</summary>
    public int DefaultRateLimitPerMinute { get; set; } = 60;
}
