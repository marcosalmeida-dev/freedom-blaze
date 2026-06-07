namespace FreedomBlaze.Constants;

public static class CacheKeys
{
    /// <summary>Cache key for a day's generated Bitcoin news set (one entry per calendar day).</summary>
    public static string BitcoinNews(DateOnly date) => $"BitcoinNews:{date:yyyy-MM-dd}";
}
