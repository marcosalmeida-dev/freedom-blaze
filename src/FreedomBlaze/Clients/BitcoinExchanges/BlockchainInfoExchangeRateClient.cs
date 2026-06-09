using System.Text.Json.Serialization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.BitcoinExchanges;

public class BlockchainInfoExchangeRateClient(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateClient
{
    public string ExchangeName => "BlockchainInfo";

    public async Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(ExchangeName);
            using var response = await httpClient.GetAsync("/ticker", cancellationToken);
            using var content = response.Content;
            var rates = await content.ReadAsJsonAsync<BlockchainInfoExchangeRates>();

            return new BitcoinExchangeRateModel { ExchangeName = ExchangeName, BitcoinRateInUSD = Math.Round(rates.USD.Sell, 0) };
        }
        catch
        {
            throw new ExchangeIntegrationException(ExchangeName);
        }
    }

    private sealed class BlockchainInfoExchangeRates
    {
        [JsonPropertyName("USD")]
        public required UsdRate USD { get; init; }
    }

    private sealed class UsdRate
    {
        [JsonPropertyName("sell")]
        public decimal Sell { get; init; }

        [JsonPropertyName("symbol")]
        public string Symbol { get; init; } = string.Empty;
    }
}
