using FreedomBlaze.Exceptions;
using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using System.Text.Json.Serialization;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class BitstampExchangeRateProvider(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateProvider
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

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = decimal.Parse(rate.Rate, System.Globalization.CultureInfo.InvariantCulture) };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }
}

public class BitstampExchangeRate
{
    [JsonPropertyName("bid")]
    public string Rate { get; set; }
}
