using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FreedomBlaze.Models;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Http.Extensions;
using System.Text.Json.Serialization;
using FreedomBlaze.Extensions;
using ReactCA.Application.Common.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges.Gemini;

public class GeminiExchangeRateProvider : IExchangeRateProvider
{
    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.gemini.com")
            };
            using var response = await httpClient.GetAsync("/v1/pubticker/btcusd", cancellationToken).ConfigureAwait(false);
            using var content = response.Content;
            var data = await content.ReadAsJsonAsync<GeminiExchangeRateInfo>().ConfigureAwait(false);

            return new BitcoinExchangeRateModel { BitcoinRateInUSD = double.Parse(data.Bid.ToRemoveDecimalCase()) };
        }
        catch
        {
            throw new ExchangeIntegrationException(nameof(GeminiExchangeRateProvider));
        }
    }

    private class GeminiExchangeRateInfo
    {
        [JsonPropertyName("bid")]
        public string Bid { get; set; }
    }
}
