using System.Net;
using System.Text.Json.Serialization;
using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.WebClients.CurrencyExchanges;

/// <summary>
/// Currency exchange-rate provider backed by exchangerate-api.com (USD-based; 1,500 free calls/month).
/// Active when "CurrencyExchange:Provider" is <see cref="CurrencyExchangeProviderType.ExchangeRateApiCom"/>.
/// </summary>
public class ExchangeRateApiComProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ExchangeRateApiComProvider> logger) : ICurrencyExchangeProvider
{
    private readonly string _apiKey = configuration["ExchangeRateApiKey"] ?? string.Empty;
    private readonly bool _isDevelopment = environment.IsDevelopment();

    public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            if (!_isDevelopment)
                throw new InvalidOperationException("ExchangeRateApiKey is required in production. Configure it in appsettings (exchangerate-api.com).");

            logger.LogWarning("ExchangeRateApiKey is not configured. Using stub data for development.");
            return BuildStubModel();
        }

        var httpClient = httpClientFactory.CreateClient("ExchangeRateApiCom");
        using var response = await httpClient.GetAsync($"v6/{_apiKey}/latest/USD", cancellationToken);
        using var content = response.Content;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Error getting currency exchange rates from the provider: {httpClient.BaseAddress}. Status code: {response.StatusCode}.");
        }

        var payload = await content.ReadAsJsonAsync<ExchangeRateApiComModel>();

        if (!string.Equals(payload.Result, "success", StringComparison.OrdinalIgnoreCase) || payload.ConversionRates == null)
        {
            throw new HttpRequestException(
                $"exchangerate-api.com returned result='{payload.Result}'. Check that the API key is valid. Base URL: {httpClient.BaseAddress}");
        }

        return MapToModel(payload.TimeLastUpdateUnix, payload.ConversionRates);
    }

    // exchangerate-api.com is already USD-based, so conversion rates map straight across (USD = 1).
    private static CurrencyExchangeRateModel MapToModel(long lastUpdateUnix, ConversionRates rates) => new()
    {
        Date = DateTimeOffset.FromUnixTimeSeconds(lastUpdateUnix).UtcDateTime,
        Rates =
        [
            new CurrencyRate { Currency = "USD", Rate = 1m },
            new CurrencyRate { Currency = "EUR", Rate = rates.EUR },
            new CurrencyRate { Currency = "GBP", Rate = rates.GBP },
            new CurrencyRate { Currency = "CHF", Rate = rates.CHF },
            new CurrencyRate { Currency = "AUD", Rate = rates.AUD },
            new CurrencyRate { Currency = "JPY", Rate = rates.JPY },
            new CurrencyRate { Currency = "ZAR", Rate = rates.ZAR },
            new CurrencyRate { Currency = "ARS", Rate = rates.ARS },
            new CurrencyRate { Currency = "BRL", Rate = rates.BRL },
        ]
    };

    // Stub rates (units per 1 USD) for local development when no API key is configured.
    private static CurrencyExchangeRateModel BuildStubModel() => MapToModel(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        new ConversionRates
        {
            USD = 1m, EUR = 0.92m, GBP = 0.79m, CHF = 0.88m, AUD = 1.52m,
            JPY = 157m, ZAR = 18.5m, ARS = 950m, BRL = 5.6m
        });

    private sealed class ExchangeRateApiComModel
    {
        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("time_last_update_unix")]
        public long TimeLastUpdateUnix { get; init; }

        [JsonPropertyName("base_code")]
        public string? BaseCode { get; init; }

        [JsonPropertyName("conversion_rates")]
        public ConversionRates? ConversionRates { get; init; }
    }

    private sealed class ConversionRates
    {
        [JsonPropertyName("USD")] public decimal USD { get; init; }
        [JsonPropertyName("EUR")] public decimal EUR { get; init; }
        [JsonPropertyName("GBP")] public decimal GBP { get; init; }
        [JsonPropertyName("CHF")] public decimal CHF { get; init; }
        [JsonPropertyName("AUD")] public decimal AUD { get; init; }
        [JsonPropertyName("JPY")] public decimal JPY { get; init; }
        [JsonPropertyName("ZAR")] public decimal ZAR { get; init; }
        [JsonPropertyName("ARS")] public decimal ARS { get; init; }
        [JsonPropertyName("BRL")] public decimal BRL { get; init; }
    }
}
