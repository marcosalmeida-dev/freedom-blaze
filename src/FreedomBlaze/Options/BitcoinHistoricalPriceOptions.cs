namespace FreedomBlaze.Options;

public sealed class BitcoinHistoricalPriceOptions
{
    public const string Section = "BitcoinHistoricalPrice";
    public string CandleBaseUrl { get; set; } = "https://api.exchange.coinbase.com/";
    public string FxBaseUrl { get; set; } = "https://api.frankfurter.dev/";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxCacheEntries { get; set; } = 512;
    public int MaxProviderRequestsPerMinute { get; set; } = 120;
    public int MaxConcurrentRequests { get; set; } = 2;
    public int MaxQueuedRequests { get; set; } = 64;
}
