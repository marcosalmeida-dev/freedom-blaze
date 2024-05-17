using FreedomBlaze.Http.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using System.Text.Json.Serialization;

namespace FreedomBlaze.WebClients.CurrencyExchanges.ExchangeRateApi
{
    public class ExchangeRateApiProvider : ICurrencyExchangeProvider
    {
        private readonly string _apiKey;
        public ExchangeRateApiProvider(IConfiguration configuration)
        {
            _apiKey = configuration["CurrencyExchangeRateApi"];
        }
        public async Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("http://api.exchangeratesapi.io")
            };
            using var response = await httpClient.GetAsync($"v1/latest?access_key={_apiKey}&symbols=USD,EUR,GBP,CHF,AUD,JPY,ZAR,ARS,BRL", cancellationToken).ConfigureAwait(false);
            using var content = response.Content;
            var currencyRates = await content.ReadAsJsonAsync<CurrencyExchangeApiModel>().ConfigureAwait(false);

            DateTime dateTime = DateTime.Now;
            DateTime.TryParse(currencyRates.Date, out dateTime);
            //The base rate for this provider is EUR, so we have to convert it to USD to calculate with the btc exchanges providers which default is USD
            var resultModel = new CurrencyExchangeRateModel()
            {
                Currency = "USD",
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
                        Currency = "EUR",
                        Rate = currencyRates.Rates.EUR / currencyRates.Rates.USD
                    },
                    new CurrencyRate()
                    {
                        Currency = "GBP",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.GBP
                    },
                    new CurrencyRate() 
                    {
                        Currency = "CHF",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.CHF
                    },
                    new CurrencyRate()
                    {
                        Currency = "AUD",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.AUD
                    },
                    new CurrencyRate()
                    {
                        Currency = "JPY",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.JPY
                    },
                     new CurrencyRate()
                    {
                        Currency = "ZAR",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.ZAR
                    },
                    new CurrencyRate()
                    {
                        Currency = "ARS",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.ARS
                    },
                    new CurrencyRate()
                    {
                        Currency = "BRL",
                        Rate = (currencyRates.Rates.EUR / currencyRates.Rates.USD) * currencyRates.Rates.BRL
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
}
