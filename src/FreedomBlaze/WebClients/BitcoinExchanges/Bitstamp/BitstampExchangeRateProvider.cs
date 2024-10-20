using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.WebClients.BitcoinExchanges.BlockchainInfo;
using ReactCA.Application.Common.Exceptions;
using System.Text.Json.Serialization;

namespace FreedomBlaze.WebClients.BitcoinExchanges.Bitstamp;

public class BitstampExchangeRateProvider : IExchangeRateProvider
{
    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://www.bitstamp.net")
            };
            using var response = await httpClient.GetAsync("api/v2/ticker/btcusd", cancellationToken);
            using var content = response.Content;
            var rate = await content.ReadAsJsonAsync<BitstampExchangeRate>();

            return new BitcoinExchangeRateModel { BitcoinRateInUSD = double.Parse(rate.Rate) };
        }
        catch
        {
            throw new ExchangeIntegrationException(nameof(BitstampExchangeRateProvider));
        }
    }
}

public class BitstampExchangeRate
{
    [JsonPropertyName("bid")]
    public string Rate { get; set; }
}
