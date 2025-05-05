using System.Net;
using System.Text.Json.Serialization;
using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze.WebClients;

public class ExchangeRateApiProvider : ICurrencyExchangeProvider
{
    private readonly string _apiKey;
    public ExchangeRateApiProvider(IConfiguration configuration)
    {
        _apiKey = configuration["CurrencyExchangeRateApi"];
    }
    public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
    {
        if (_apiKey == null)
        {
            throw new ArgumentNullException("CurrencyExchangeRateApi key is missing in the configuration file");
        }

        CurrencyExchangeApiModel currencyRates;

        if (string.IsNullOrEmpty(_apiKey))
        {
            // Return test result in debug mode
            currencyRates = new CurrencyExchangeApiModel()
            {
                Success = true,
                Timestamp = 1620000000,
                Base = "EUR",
                Date = "2021-05-03",
                Rates = new Rates()
                {
                    USD = 1.2m,
                    EUR = 1,
                    GBP = 0.8m,
                    CHF = 1.1m,
                    AUD = 1.5m,
                    JPY = 130m,
                    ZAR = 20m,
                    ARS = 100m,
                    BRL = 6.0m
                }
            };
        }
        else
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.exchangeratesapi.io")
            };
            using var response = await httpClient.GetAsync($"v1/latest?access_key={_apiKey}&symbols=USD,EUR,GBP,CHF,AUD,JPY,ZAR,ARS,BRL", cancellationToken);
            using var content = response.Content;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"Error getting currency exchange rates from the provider: {httpClient.BaseAddress}. Status code: {response.StatusCode}. Response content: {response.Content}");
            }

            currencyRates = await content.ReadAsJsonAsync<CurrencyExchangeApiModel>();
        }

        DateTime dateTime = DateTime.Now;
        DateTime.TryParse(currencyRates.Date, out dateTime);
        //The base rate for this provider is EUR, so we have to convert it to USD to calculate with the btc exchanges providers which default is USD
        var resultModel = new CurrencyExchangeRateModel()
        {
            Date = dateTime,
            Rates = new List<CurrencyRate>()
            {
                new CurrencyRate()
                {
                    Currency = "USD",
                    Rate = 1
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.EUR),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD)
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.GBP),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.GBP)
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.CHF),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.CHF)
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.AUD),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.AUD)
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.JPY),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.JPY)
                },
                 new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.ZAR),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.ZAR)
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.ARS),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.ARS)
                },
                new CurrencyRate()
                {
                    Currency = nameof(currencyRates.Rates.BRL),
                    Rate = (double)(currencyRates.Rates.EUR / currencyRates.Rates.USD * currencyRates.Rates.BRL)
                }
            }
        };

        return resultModel;
    }
}

public class Rates
{
    [JsonPropertyName("USD")]
    public decimal USD { get; set; }

    [JsonPropertyName("EUR")]
    public int EUR { get; set; }

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
