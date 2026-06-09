using System.Globalization;
using System.Text.Json.Serialization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.BitcoinExchanges;

public class GeminiExchangeRateClient(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateClient
{
    public string ExchangeName => "Gemini";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            using var response = await httpClient.GetAsync("/v1/pubticker/btcusd", cancellationToken);
            using var content = response.Content;
            var data = await content.ReadAsJsonAsync<GeminiExchangeRateInfo>();

            return new BitcoinExchangeRateModel
            {
                ExchangeName = ExchangeName,
                BitcoinRateInUSD = decimal.Parse(data.Bid.ToRemoveDecimalCase(), CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private sealed class GeminiExchangeRateInfo
    {
        [JsonPropertyName("bid")]
        public string Bid { get; init; } = string.Empty;
    }
}
