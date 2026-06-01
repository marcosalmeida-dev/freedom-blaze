using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.WebClients.CurrencyExchanges;

/// <summary>
/// Caching facade over the configured <see cref="ICurrencyExchangeProvider"/>. The active provider and the
/// cache duration are both driven by the "CurrencyExchange" section, so switching providers is a single
/// configuration change (see <see cref="CurrencyExchangeOptions"/>).
/// </summary>
public class CurrencyExchangeRateProviders : ICurrencyExchangeProvider
{
    private readonly ICurrencyExchangeProvider _provider;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheDuration;

    public CurrencyExchangeRateProviders(
        IServiceProvider serviceProvider,
        IMemoryCache cache,
        IOptions<CurrencyExchangeOptions> options,
        ILogger<CurrencyExchangeRateProviders> logger)
    {
        var settings = options.Value;
        _cache = cache;
        _cacheDuration = settings.CacheDuration;
        _provider = serviceProvider.GetRequiredKeyedService<ICurrencyExchangeProvider>(settings.Provider);

        logger.LogInformation(
            "Currency exchange provider '{Provider}' active (cache duration {CacheDuration}).",
            settings.Provider, settings.CacheDuration);
    }

    public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
    {
        const string cacheKey = nameof(GetCurrencyRate);
        if (!_cache.TryGetValue(cacheKey, out CurrencyExchangeRateModel? currencyExchangeRateResult))
        {
            currencyExchangeRateResult = await _provider.GetCurrencyRate(cancellationToken);

            if (currencyExchangeRateResult != null)
            {
                _cache.Set(cacheKey, currencyExchangeRateResult, _cacheDuration);
            }
        }

        return currencyExchangeRateResult!;
    }
}
