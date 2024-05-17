using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.WebClients.BitcoinExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges.ExchangeRateApi;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.WebClients.CurrencyExchanges;

public class CurrencyExchangeRateProviders : ICurrencyExchangeProvider
{
    private IConfiguration _configuration;
    private IMemoryCache _cache;

    public CurrencyExchangeRateProviders(IConfiguration configuration, IMemoryCache cache)
    {
        _configuration = configuration;
        _cache = cache;
    }

    public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
    {
        var cacheKey = nameof(GetCurrencyRate);
        if (!_cache.TryGetValue(cacheKey, out CurrencyExchangeRateModel currencyExchangeRateResult))
        {
            ExchangeRateApiProvider exchangeRateApiProvider = new ExchangeRateApiProvider(_configuration); 

            currencyExchangeRateResult = await exchangeRateApiProvider.GetCurrencyRate(cancellationToken);

            if (currencyExchangeRateResult != null)
            {
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromHours(6));

                _cache.Set(cacheKey, currencyExchangeRateResult, cacheEntryOptions);
            }
        }

        return currencyExchangeRateResult;
    }
}
