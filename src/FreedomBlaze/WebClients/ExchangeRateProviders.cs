using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Logging;
using FreedomBlaze.Models;
using FreedomBlaze.WebClients.BitcoinExchanges;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.WebClients;

public class ExchangeRateProvider : IExchangeRateProvider
{
    private readonly IExchangeRateProvider[] _exchangeRateProviders =
    {
        new BlockchainInfoExchangeRateProvider(),
        new BitstampExchangeRateProvider(),
        new CoinGeckoExchangeRateProvider(),
        new CoinbaseExchangeRateProvider(),
        new GeminiExchangeRateProvider(),
        new CoingateExchangeRateProvider()
    };

    private readonly IMemoryCache _cache;
    private readonly ICurrencyExchangeProvider _currencyExchangeProvider;

    public static TimeOnly LastUpdateTime { get; set; }

    public string ExchangeName => "ExchangeRateProvider";

    public static List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList = new List<BitcoinExchangeStatusModel>();

    public ExchangeRateProvider(IMemoryCache cache, ICurrencyExchangeProvider currencyExchangeProvider)
    {
        _cache = cache;
        _currencyExchangeProvider = currencyExchangeProvider;
    }

    public Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        var timeNow = new TimeOnly(DateTime.Now.Hour, DateTime.Now.Minute, DateTime.Now.Second);
        return _cache.GetOrCreateAsync(timeNow, async e =>
        {
            e.SetOptions(new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
            });

            var cacheKey = nameof(GetExchangeRateAsync);

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
                    exchangeRateAvgResult = exchangeRates.Where(w => w.IsSuccess && w.Result != null && w.Result.BitcoinRateInUSD > 0).Average(a => a.Result.BitcoinRateInUSD);
                    var cacheEntryOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromMinutes(1));

                    _cache.Set(cacheKey, exchangeRateAvgResult, cacheEntryOptions);

                    BitcoinExchangeStatusList = exchangeRates.Select(s => new BitcoinExchangeStatusModel
                    {
                        ExchangeName = s.IsSuccess ? s.Result.ExchangeName : ((ExchangeIntegrationException)s.Exception.InnerException).ExchangeName,
                        IsExchangeAvailable = s.IsSuccess
                    }).ToList();

                    var failedExchangesTasks = exchangeRates.Where(w => w.IsSuccess == false).ToList();
                    foreach (var failedTask in failedExchangesTasks)
                    {
                        ExchangeIntegrationException exchangeRateEx = failedTask.Exception.InnerException as ExchangeIntegrationException;
                        Logger.LogError(exchangeRateEx, $"GetExchangeRate FAILED for: {exchangeRateEx.ExchangeName}");
                    }

                    LastUpdateTime = timeNow;
                }
            }

            BitcoinExchangeRateModel exchangeRateModelResult = null;
            if (exchangeRateAvgResult.HasValue)
            {
                var currentCurrency = CurrencyModel.CurrentAppCurrency.Value;
                CurrencyExchangeRateModel currencyRatesResult = await _currencyExchangeProvider.GetCurrencyRate(cancellationToken);

                exchangeRateModelResult = new BitcoinExchangeRateModel()
                {
                    //BitcoinRateInUSD = double.Truncate(currencyRatesResult[currentCurrency].Rate * exchangeRateAvgResult.Value),
                    BitcoinRateInUSD = exchangeRateAvgResult.Value,
                    CurrencyExchangeRate = currencyRatesResult
                };
            }

            return exchangeRateModelResult;
        });
    }

}
