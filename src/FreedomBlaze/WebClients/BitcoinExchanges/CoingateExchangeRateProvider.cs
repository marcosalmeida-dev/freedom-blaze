using FreedomBlaze.Models;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Extensions;
using FreedomBlaze.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class CoingateExchangeRateProvider(IHttpClientFactory httpClientFactory) : IExchangeRateProvider
{
    public string ExchangeName => "Coingate";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            var response = await httpClient.GetStringAsync("/v2/rates/merchant/BTC/USD", cancellationToken);

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = double.Parse(response.ToRemoveDecimalCase()) };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }
}
