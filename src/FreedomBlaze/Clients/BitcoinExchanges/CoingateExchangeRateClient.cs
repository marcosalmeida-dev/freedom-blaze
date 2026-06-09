using System.Globalization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.BitcoinExchanges;

public class CoingateExchangeRateClient(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateClient
{
    public string ExchangeName => "Coingate";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            var response = await httpClient.GetStringAsync("/v2/rates/merchant/BTC/USD", cancellationToken);

            return new BitcoinExchangeRateModel
            {
                ExchangeName = ExchangeName,
                BitcoinRateInUSD = decimal.Parse(response.ToRemoveDecimalCase(), CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }
}
