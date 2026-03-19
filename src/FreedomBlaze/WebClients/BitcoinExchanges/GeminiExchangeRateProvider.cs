using FreedomBlaze.Models;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Http.Extensions;
using System.Text.Json.Serialization;
using FreedomBlaze.Extensions;
using FreedomBlaze.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class GeminiExchangeRateProvider(IHttpClientFactory httpClientFactory) : IExchangeRateProvider
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

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = double.Parse(data.Bid.ToRemoveDecimalCase()) };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private class GeminiExchangeRateInfo
    {
        [JsonPropertyName("bid")]
        public string Bid { get; set; }
    }
}
