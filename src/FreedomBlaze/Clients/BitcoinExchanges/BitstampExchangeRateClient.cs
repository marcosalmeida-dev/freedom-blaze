using System.Globalization;
using System.Text.Json.Serialization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.BitcoinExchanges;

public class BitstampExchangeRateClient(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateClient
{
    public string ExchangeName => "Bitstamp";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            using var response = await httpClient.GetAsync("api/v2/ticker/btcusd", cancellationToken);
            using var content = response.Content;
            var rate = await content.ReadAsJsonAsync<BitstampExchangeRate>();

            return new BitcoinExchangeRateModel
            {
                ExchangeName = ExchangeName,
                BitcoinRateInUSD = decimal.Parse(rate.Rate, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private sealed class BitstampExchangeRate
    {
        [JsonPropertyName("bid")]
        public string Rate { get; init; } = string.Empty;
    }
}
