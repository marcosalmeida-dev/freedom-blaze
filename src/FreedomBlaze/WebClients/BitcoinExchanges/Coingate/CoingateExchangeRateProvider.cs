using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FreedomBlaze.Models;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Extensions;
using FreedomBlaze.WebClients.BitcoinExchanges.CoinGecko;
using ReactCA.Application.Common.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges.Coingate;

public class CoingateExchangeRateProvider : IExchangeRateProvider
{
    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.coingate.com")
            };
            var response = await httpClient.GetStringAsync("/v2/rates/merchant/BTC/USD", cancellationToken)
                ;

            return new BitcoinExchangeRateModel { BitcoinRateInUSD = double.Parse(response.ToRemoveDecimalCase()) };
        }
        catch
        {
            throw new ExchangeIntegrationException(nameof(CoingateExchangeRateProvider));
        }
    }
}
