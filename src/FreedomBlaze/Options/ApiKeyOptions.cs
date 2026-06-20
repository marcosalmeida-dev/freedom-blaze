namespace FreedomBlaze.Options;

public class ApiKeyOptions
{
    public const string Section = "ApiKeys";

    /// <summary>Default per-key request budget per minute when a key does not specify its own.</summary>
    public int DefaultRateLimitPerMinute { get; set; } = 60;
}
