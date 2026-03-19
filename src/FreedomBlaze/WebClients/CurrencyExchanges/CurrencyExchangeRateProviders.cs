using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.WebClients.CurrencyExchanges;

public class CurrencyExchangeRateProviders(ExchangeRateApiProvider exchangeRateApiProvider, IMemoryCache cache) : ICurrencyExchangeProvider
{
    public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
    {
        const string cacheKey = nameof(GetCurrencyRate);
        if (!cache.TryGetValue(cacheKey, out CurrencyExchangeRateModel? currencyExchangeRateResult))
        {
            currencyExchangeRateResult = await exchangeRateApiProvider.GetCurrencyRate(cancellationToken);

            if (currencyExchangeRateResult != null)
            {
                cache.Set(cacheKey, currencyExchangeRateResult, TimeSpan.FromHours(6));
            }
        }

        return currencyExchangeRateResult!;
    }
}
