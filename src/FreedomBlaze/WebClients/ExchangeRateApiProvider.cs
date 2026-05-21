using System.Net;
using System.Text.Json.Serialization;
using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.WebClients;

public class ExchangeRateApiProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ExchangeRateApiProvider> logger) : ICurrencyExchangeProvider
{
    private readonly string _apiKey = configuration["CurrencyExchangeRateApi"] ?? string.Empty;
    private readonly bool _isDevelopment = environment.IsDevelopment();

    public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            if (!_isDevelopment)
                throw new InvalidOperationException("CurrencyExchangeRateApi key is required in production. Configure it in appsettings.");

            logger.LogWarning("CurrencyExchangeRateApi key is not configured. Using stub data for development.");
        }

        CurrencyExchangeApiModel currencyRates;

        if (string.IsNullOrEmpty(_apiKey))
        {
            // Stub rates for development only
            currencyRates = new CurrencyExchangeApiModel
            {
                Success = true,
                Timestamp = 1620000000,
                Base = "EUR",
                Date = DateTime.Today.ToString("yyyy-MM-dd"),
                Rates = new Rates { USD = 1.2m, EUR = 1, GBP = 0.8m, CHF = 1.1m, AUD = 1.5m, JPY = 130m, ZAR = 20m, ARS = 100m, BRL = 6.25m }
            };
        }
        else
        {
            var httpClient = httpClientFactory.CreateClient("ExchangeRateApi");
            using var response = await httpClient.GetAsync($"v1/latest?access_key={_apiKey}&symbols=USD,EUR,GBP,CHF,AUD,JPY,ZAR,ARS,BRL", cancellationToken);
            using var content = response.Content;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"Error getting currency exchange rates from the provider: {httpClient.BaseAddress}. Status code: {response.StatusCode}. Response content: {response.Content}");
            }

            currencyRates = await content.ReadAsJsonAsync<CurrencyExchangeApiModel>();

            if (!currencyRates.Success || currencyRates.Rates == null)
            {
                throw new HttpRequestException(
                    $"Currency exchange API returned success=false. Check if your API plan supports HTTPS or if the key is valid. Base URL: {httpClient.BaseAddress}");
            }
        }

        DateTime dateTime = DateTime.Now;
        DateTime.TryParse(currencyRates.Date, out dateTime);
        // The base rate for this provider is EUR; convert to USD to match BTC exchange providers
        var eurUsd = currencyRates.Rates.EUR / currencyRates.Rates.USD;
        var resultModel = new CurrencyExchangeRateModel
        {
            Date = dateTime,
            Rates =
            [
                new CurrencyRate { Currency = "USD", Rate = 1m },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.EUR), Rate = eurUsd },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.GBP), Rate = eurUsd * currencyRates.Rates.GBP },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.CHF), Rate = eurUsd * currencyRates.Rates.CHF },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.AUD), Rate = eurUsd * currencyRates.Rates.AUD },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.JPY), Rate = eurUsd * currencyRates.Rates.JPY },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.ZAR), Rate = eurUsd * currencyRates.Rates.ZAR },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.ARS), Rate = eurUsd * currencyRates.Rates.ARS },
                new CurrencyRate { Currency = nameof(currencyRates.Rates.BRL), Rate = eurUsd * currencyRates.Rates.BRL },
            ]
        };

        return resultModel;
    }
}

public class Rates
{
    [JsonPropertyName("USD")]
    public decimal USD { get; set; }

    [JsonPropertyName("EUR")]
    public decimal EUR { get; set; }

    [JsonPropertyName("GBP")]
    public decimal GBP { get; set; }

    [JsonPropertyName("CHF")]
    public decimal CHF { get; set; }

    [JsonPropertyName("AUD")]
    public decimal AUD { get; set; }

    [JsonPropertyName("JPY")]
    public decimal JPY { get; set; }

    [JsonPropertyName("ZAR")]
    public decimal ZAR { get; set; }

    [JsonPropertyName("ARS")]
    public decimal ARS { get; set; }

    [JsonPropertyName("BRL")]
    public decimal BRL { get; set; }
}

public class CurrencyExchangeApiModel
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("timestamp")]
    public int Timestamp { get; set; }

    [JsonPropertyName("base")]
    public string Base { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }

    [JsonPropertyName("rates")]
    public Rates Rates { get; set; }
}
