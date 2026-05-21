using System.Text.Json.Serialization;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.WebClients.BitcoinExchanges;

public class BlockchainInfoExchangeRateProvider(IHttpClientFactory httpClientFactory) : IBitcoinExchangeRateProvider
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

    public class USD
    {
        [JsonPropertyName("sell")]
        public decimal Sell { get; set; }

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; }
    }

    private class BlockchainInfoExchangeRates
    {
        [JsonPropertyName("USD")]
        public required USD USD { get; init; }
    }
}
