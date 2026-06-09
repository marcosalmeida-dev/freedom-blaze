using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.Services;

/// <summary>
/// Aggregates every <see cref="IBitcoinExchangeRateClient"/> into a single averaged BTC/USD rate
/// (cached briefly), attaches the latest fiat conversion rates from
/// <see cref="ICurrencyExchangeRateClient"/>, and records per-exchange availability for the status UI.
/// </summary>
public class ExchangeRateService(
    IMemoryCache cache,
    ICurrencyExchangeRateClient currencyExchangeClient,
    IEnumerable<IBitcoinExchangeRateClient> bitcoinExchangeClients) : IExchangeRateService
{
    public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; private set; } = [];

    public async Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = nameof(GetExchangeRateAsync);

        if (!cache.TryGetValue(cacheKey, out decimal? exchangeRateAvgResult))
        {
            var tasks = bitcoinExchangeClients.Select(p => p.GetExchangeRateAsync(cancellationToken));
            var exchangeRates = await tasks.WhenAllOrException();

            if (exchangeRates != null)
            {
                var successRates = exchangeRates
                    .Where(w => w.IsSuccess && w.Result is { BitcoinRateInUSD: > 0 })
                    .ToList();

                if (successRates.Count > 0)
                    exchangeRateAvgResult = successRates.Average(a => a.Result!.BitcoinRateInUSD);

                cache.Set(cacheKey, exchangeRateAvgResult, TimeSpan.FromSeconds(55));

                BitcoinExchangeStatusList = exchangeRates.Select(s => new BitcoinExchangeStatusModel
                {
                    ExchangeName = s.IsSuccess
                        ? s.Result!.ExchangeName
                        : (s.Exception?.InnerException as ExchangeIntegrationException)?.ExchangeName,
                    IsExchangeAvailable = s.IsSuccess
                }).ToList();
            }
        }

        if (!exchangeRateAvgResult.HasValue)
            return null;

        CurrencyExchangeRateModel? currencyRatesResult;
        try
        {
            currencyRatesResult = await currencyExchangeClient.GetCurrencyRateAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Currency rate failure is non-fatal — BTC/USD price still shows; non-USD rates update next tick.
            return null;
        }

        if (currencyRatesResult == null)
            return null;

        return new BitcoinExchangeRateModel
        {
            BitcoinRateInUSD = exchangeRateAvgResult.Value,
            CurrencyExchangeRate = currencyRatesResult
        };
    }
}
