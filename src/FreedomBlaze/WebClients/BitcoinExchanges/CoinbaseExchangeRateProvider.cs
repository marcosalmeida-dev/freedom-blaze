using FreedomBlaze.Models;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Http.Extensions;
using System.Text.Json.Serialization;
using FreedomBlaze.Extensions;
using FreedomBlaze.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class CoinbaseExchangeRateProvider(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateProvider
{
    public string ExchangeName => "Coinbase";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            using var response = await httpClient.GetAsync("/v2/exchange-rates?currency=BTC", cancellationToken);
            using var content = response.Content;
            var wrapper = await content.ReadAsJsonAsync<DataWrapper>();

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = decimal.Parse(wrapper.Data.Rates.USD.ToRemoveDecimalCase()) };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private class DataWrapper
    {
        [JsonPropertyName("data")]
        public required CoinbaseExchangeRate Data { get; init; }

        public class CoinbaseExchangeRate
        {
            [JsonPropertyName("rates")]
            public required ExchangeRates Rates { get; init; }

            public class ExchangeRates
            {
                [JsonPropertyName("USD")]
                public string USD { get; init; }
            }
        }
    }
}
