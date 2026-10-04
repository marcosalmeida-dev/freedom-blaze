namespace FreedomBlaze.Options;

public sealed class BitcoinTrackingOptions
{
    public const string Section = "BitcoinTracking";
    public string BaseUrl { get; set; } = "https://mempool.space/api/";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxCacheEntries { get; set; } = 256;
    public int MaxProviderRequestsPerMinute { get; set; } = 60;
    public int MaxConcurrentRequests { get; set; } = 4;
}
