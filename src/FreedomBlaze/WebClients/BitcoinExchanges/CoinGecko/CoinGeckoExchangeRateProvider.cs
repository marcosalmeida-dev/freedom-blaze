using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using ReactCA.Application.Common.Exceptions;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace FreedomBlaze.WebClients.BitcoinExchanges.CoinGecko;

public class CoinGeckoExchangeRateProvider : IExchangeRateProvider
{
    public static readonly Version ClientVersion = new(2, 0, 5, 0);
    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.coingecko.com")
            };
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("aa", ClientVersion.ToString()));
            using var response = await httpClient.GetAsync("api/v3/coins/markets?vs_currency=usd&ids=bitcoin", cancellationToken);
            using var content = response.Content;
            var rates = await content.ReadAsJsonAsync<CoinGeckoExchangeRate[]>();

            return new BitcoinExchangeRateModel { BitcoinRateInUSD = rates[0].Rate };
        }
        catch
        {
            throw new ExchangeIntegrationException(nameof(CoinGeckoExchangeRateProvider));
        }
    }
}

public class CoinGeckoExchangeRate
{
    [JsonPropertyName("current_price")]
    public double Rate { get; set; }
}
