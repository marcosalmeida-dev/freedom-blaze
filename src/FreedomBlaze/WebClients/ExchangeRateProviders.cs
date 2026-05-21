using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.WebClients;

public class ExchangeRateProvider : IExchangeRateProvider
{
    private readonly IMemoryCache _cache;
    private readonly ICurrencyExchangeProvider _currencyExchangeProvider;
    private readonly IEnumerable<IBitcoinExchangeRateProvider> _exchangeRateProviders;

    public string ExchangeName => "ExchangeRateProvider";

    public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; private set; } = [];

    public ExchangeRateProvider(
        IMemoryCache cache,
        ICurrencyExchangeProvider currencyExchangeProvider,
        IEnumerable<IBitcoinExchangeRateProvider> exchangeRateProviders)
    {
        _cache = cache;
        _currencyExchangeProvider = currencyExchangeProvider;
        _exchangeRateProviders = exchangeRateProviders;
    }

    public async Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = nameof(GetExchangeRateAsync);

        if (!_cache.TryGetValue(cacheKey, out decimal? exchangeRateAvgResult))
        {
            var tasks = _exchangeRateProviders.Select(p => p.GetExchangeRateAsync(cancellationToken));
            var exchangeRates = await tasks.WhenAllOrException();

            if (exchangeRates != null)
            {
                var successRates = exchangeRates.Where(w => w.IsSuccess && w.Result != null && w.Result.BitcoinRateInUSD > 0).ToList();
                if (successRates.Count > 0)
                    exchangeRateAvgResult = successRates.Average(a => a.Result.BitcoinRateInUSD);

                _cache.Set(cacheKey, exchangeRateAvgResult, TimeSpan.FromSeconds(55));

                BitcoinExchangeStatusList = exchangeRates.Select(s => new BitcoinExchangeStatusModel
                {
                    ExchangeName = s.IsSuccess ? s.Result.ExchangeName : (s.Exception?.InnerException as ExchangeIntegrationException)?.ExchangeName,
                    IsExchangeAvailable = s.IsSuccess
                }).ToList();
            }
        }

        if (!exchangeRateAvgResult.HasValue)
            return null;

        var currentCurrency = CurrencyModel.CurrentAppCurrency.Value;
        CurrencyExchangeRateModel currencyRatesResult = await _currencyExchangeProvider.GetCurrencyRate(cancellationToken);

        return new BitcoinExchangeRateModel
        {
            BitcoinRateInUSD = exchangeRateAvgResult.Value,
            CurrencyExchangeRate = currencyRatesResult
        };
    }
}
