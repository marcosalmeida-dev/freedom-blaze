using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Globalization;
using System.Text.Json.Serialization;
using FreedomBlaze.WebClients.BitcoinExchanges.Gemini;
using ReactCA.Application.Common.Exceptions;

namespace FreedomBlaze.WebClients.BitcoinExchanges.BlockchainInfo;

public class BlockchainInfoExchangeRateProvider : IExchangeRateProvider
{
    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://blockchain.info")
            };
            using var response = await httpClient.GetAsync("/ticker", cancellationToken);
            using var content = response.Content;
            var rates = await content.ReadAsJsonAsync<BlockchainInfoExchangeRates>();

            return new BitcoinExchangeRateModel { BitcoinRateInUSD = Math.Round(rates.USD.Sell, 0) };
        }
        catch
        {
            throw new ExchangeIntegrationException(nameof(BlockchainInfoExchangeRateProvider));
        }
    }

    public class USD
    {
        [JsonPropertyName("15m")]
        public double _15m { get; set; }

        [JsonPropertyName("last")]
        public double Last { get; set; }

        [JsonPropertyName("buy")]
        public double Buy { get; set; }

        [JsonPropertyName("sell")]
        public double Sell { get; set; }

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; }
    }

    private class BlockchainInfoExchangeRates
    {
        [JsonPropertyName("USD")]
        public required USD USD { get; init; }
    }
}
