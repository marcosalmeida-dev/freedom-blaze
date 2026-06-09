using System.Net;
using System.Text.Json.Serialization;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.Clients.CurrencyExchanges;

/// <summary>
/// Currency exchange-rate client backed by exchangeratesapi.io (EUR-based). Active when
/// "CurrencyExchange:Provider" is <see cref="CurrencyExchangeClientType.ExchangeRatesApiIo"/>.
/// </summary>
public class ExchangeRatesApiIoClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ExchangeRatesApiIoClient> logger) : ICurrencyExchangeRateClient
{
    private readonly string _apiKey = configuration["CurrencyExchangeRateApi"] ?? string.Empty;
    private readonly bool _isDevelopment = environment.IsDevelopment();

    public async Task<CurrencyExchangeRateModel> GetCurrencyRateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            if (!_isDevelopment)
                throw new InvalidOperationException("CurrencyExchangeRateApi key is required in production. Configure it in appsettings.");

            logger.LogWarning("CurrencyExchangeRateApi key is not configured. Using stub data for development.");
            return MapToModel(DateTime.Today, BuildStubRates());
        }

        var httpClient = httpClientFactory.CreateClient("ExchangeRateApi");
        using var response = await httpClient.GetAsync(
            $"v1/latest?access_key={_apiKey}&symbols=USD,EUR,GBP,CHF,AUD,JPY,ZAR,ARS,BRL", cancellationToken);
        using var content = response.Content;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Error getting currency exchange rates from the provider: {httpClient.BaseAddress}. Status code: {response.StatusCode}.");
        }

        var payload = await content.ReadAsJsonAsync<ExchangeRatesApiIoModel>();

        if (!payload.Success || payload.Rates == null)
        {
            throw new HttpRequestException(
                $"Currency exchange API returned success=false. Check if your API plan supports HTTPS or if the key is valid. Base URL: {httpClient.BaseAddress}");
        }

        DateTime.TryParse(payload.Date, out var date);
        return MapToModel(date, payload.Rates);
    }

    // The provider's base currency is EUR; convert every rate to USD to match the BTC exchange clients.
    private static CurrencyExchangeRateModel MapToModel(DateTime date, Rates rates)
    {
        var eurUsd = rates.EUR / rates.USD;
        return new CurrencyExchangeRateModel
        {
            Date = date,
            Rates =
            [
                new CurrencyRate { Currency = "USD", Rate = 1m },
                new CurrencyRate { Currency = "EUR", Rate = eurUsd },
                new CurrencyRate { Currency = "GBP", Rate = eurUsd * rates.GBP },
                new CurrencyRate { Currency = "CHF", Rate = eurUsd * rates.CHF },
                new CurrencyRate { Currency = "AUD", Rate = eurUsd * rates.AUD },
                new CurrencyRate { Currency = "JPY", Rate = eurUsd * rates.JPY },
                new CurrencyRate { Currency = "ZAR", Rate = eurUsd * rates.ZAR },
                new CurrencyRate { Currency = "ARS", Rate = eurUsd * rates.ARS },
                new CurrencyRate { Currency = "BRL", Rate = eurUsd * rates.BRL },
            ]
        };
    }

    // Stub rates (EUR-based, as returned by the live API) for local development when no key is set.
    private static Rates BuildStubRates() => new()
    {
        USD = 1.2m, EUR = 1m, GBP = 0.8m, CHF = 1.1m, AUD = 1.5m,
        JPY = 130m, ZAR = 20m, ARS = 100m, BRL = 6.25m
    };

    private sealed class ExchangeRatesApiIoModel
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
        [JsonPropertyName("base")] public string? Base { get; init; }
        [JsonPropertyName("date")] public string? Date { get; init; }
        [JsonPropertyName("rates")] public Rates? Rates { get; init; }
    }

    private sealed class Rates
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
