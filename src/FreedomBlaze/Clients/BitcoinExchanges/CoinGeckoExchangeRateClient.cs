using System.Text.Json.Serialization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.BitcoinExchanges;

public class CoinGeckoExchangeRateClient(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateClient
{
    public string ExchangeName => "CoinGecko";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            using var response = await httpClient.GetAsync("api/v3/coins/markets?vs_currency=usd&ids=bitcoin", cancellationToken);
            using var content = response.Content;
            var rates = await content.ReadAsJsonAsync<CoinGeckoExchangeRate[]>();

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = rates[0].Rate };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private sealed class CoinGeckoExchangeRate
    {
        [JsonPropertyName("current_price")]
        public decimal Rate { get; init; }
    }
}
