using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Caching decorator over the configured <see cref="ICurrencyExchangeRateClient"/>. The active client and
/// the cache duration are both driven by the "CurrencyExchange" section, so switching providers is a single
/// configuration change (see <see cref="CurrencyExchangeOptions"/>).
/// </summary>
public class CurrencyExchangeRateService : ICurrencyExchangeRateClient
{
    private readonly ICurrencyExchangeRateClient _client;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheDuration;

    public CurrencyExchangeRateService(
        IServiceProvider serviceProvider,
        IMemoryCache cache,
        IOptions<CurrencyExchangeOptions> options,
        ILogger<CurrencyExchangeRateService> logger)
    {
        var settings = options.Value;
        _cache = cache;
        _cacheDuration = settings.CacheDuration;
        _client = serviceProvider.GetRequiredKeyedService<ICurrencyExchangeRateClient>(settings.Provider);

        logger.LogInformation(
            "Currency exchange client '{Provider}' active (cache duration {CacheDuration}).",
            settings.Provider, settings.CacheDuration);
    }

    public async Task<CurrencyExchangeRateModel> GetCurrencyRateAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = nameof(GetCurrencyRateAsync);
        if (!_cache.TryGetValue(cacheKey, out CurrencyExchangeRateModel? result))
        {
            result = await _client.GetCurrencyRateAsync(cancellationToken);

            if (result != null)
            {
                _cache.Set(cacheKey, result, _cacheDuration);
            }
        }

        return result!;
    }
}
