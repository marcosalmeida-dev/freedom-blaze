using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FreedomBlaze.Models;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Extensions;
using FreedomBlaze.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class CoingateExchangeRateProvider : IExchangeRateProvider
{
    public string ExchangeName { get => "Coingate"; }

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

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = double.Parse(response.ToRemoveDecimalCase()) };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }
}
