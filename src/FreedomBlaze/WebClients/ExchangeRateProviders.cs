using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.WebClients.BitcoinExchanges;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.WebClients;

public class ExchangeRateProvider : IExchangeRateProvider
{
    private readonly IMemoryCache _cache;
    private readonly ICurrencyExchangeProvider _currencyExchangeProvider;
    private readonly IExchangeRateProvider[] _exchangeRateProviders;

    public string ExchangeName => "ExchangeRateProvider";

    public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; private set; } = [];

    public ExchangeRateProvider(
        IMemoryCache cache,
        ICurrencyExchangeProvider currencyExchangeProvider,
        IHttpClientFactory httpClientFactory)
    {
        _cache = cache;
        _currencyExchangeProvider = currencyExchangeProvider;
        _exchangeRateProviders =
        [
            new BlockchainInfoExchangeRateProvider(httpClientFactory),
            new BitstampExchangeRateProvider(httpClientFactory),
            new CoinGeckoExchangeRateProvider(httpClientFactory),
            new CoinbaseExchangeRateProvider(httpClientFactory),
            new GeminiExchangeRateProvider(httpClientFactory),
            new CoingateExchangeRateProvider(httpClientFactory)
        ];
    }

    public async Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = nameof(GetExchangeRateAsync);

        if (!_cache.TryGetValue(cacheKey, out double? exchangeRateAvgResult))
        {
            var tasks = new List<Task<BitcoinExchangeRateModel>>();

            foreach (var provider in _exchangeRateProviders)
            {
                tasks.Add(provider.GetExchangeRateAsync(cancellationToken));
            }

            var exchangeRates = await tasks.WhenAllOrException();

            if (exchangeRates != null)
            {
                exchangeRateAvgResult = exchangeRates.Where(w => w.IsSuccess && w.Result != null && w.Result.BitcoinRateInUSD > 0)
                                                     .Average(a => a.Result.BitcoinRateInUSD);

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
