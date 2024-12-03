using FreedomBlaze.Exceptions;
using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using System.Text.Json.Serialization;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class BitstampExchangeRateProvider : IExchangeRateProvider
{
    public string ExchangeName { get => "Bitstamp"; }

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

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName,  BitcoinRateInUSD = double.Parse(rate.Rate) };
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
