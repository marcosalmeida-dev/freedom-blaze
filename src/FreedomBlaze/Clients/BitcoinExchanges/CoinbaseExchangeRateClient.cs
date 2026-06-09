using System.Globalization;
using System.Text.Json.Serialization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.BitcoinExchanges;

public class CoinbaseExchangeRateClient(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateClient
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

            return new BitcoinExchangeRateModel
            {
                ExchangeName = ExchangeName,
                BitcoinRateInUSD = decimal.Parse(wrapper.Data.Rates.USD.ToRemoveDecimalCase(), CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private sealed class DataWrapper
    {
        [JsonPropertyName("data")]
        public required CoinbaseExchangeRate Data { get; init; }

        public sealed class CoinbaseExchangeRate
        {
            [JsonPropertyName("rates")]
            public required ExchangeRates Rates { get; init; }

            public sealed class ExchangeRates
            {
                [JsonPropertyName("USD")]
                public string USD { get; init; } = string.Empty;
            }
        }
    }
}
