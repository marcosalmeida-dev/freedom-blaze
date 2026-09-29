namespace FreedomBlaze.Constants;

public static class CacheKeys
{
    /// <summary>Cache key for a day's generated Bitcoin news set (one entry per calendar day).</summary>
    public static string BitcoinNews(DateOnly date) => $"BitcoinNews:{date:yyyy-MM-dd}";

    /// <summary>
    /// Negative-cache key holding the reader-facing message from a day's most recent failed
    /// generation. Its presence is what puts that day on cooldown.
    /// </summary>
    public static string BitcoinNewsFailure(DateOnly date) => $"BitcoinNews:Failed:{date:yyyy-MM-dd}";

    /// <summary>Provider-wide configuration or billing failures affect every requested date.</summary>
    public const string BitcoinNewsProviderFailure = "BitcoinNews:ProviderFailed";
}
