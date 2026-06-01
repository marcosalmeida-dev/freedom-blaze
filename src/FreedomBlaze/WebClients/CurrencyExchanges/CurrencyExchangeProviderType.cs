namespace FreedomBlaze.WebClients.CurrencyExchanges;

/// <summary>
/// Selects which fiat currency exchange-rate API supplies conversion rates. Used as the DI key for the
/// keyed <see cref="FreedomBlaze.Interfaces.ICurrencyExchangeProvider"/> registrations and chosen at runtime
/// via the "CurrencyExchange:Provider" configuration value.
/// </summary>
public enum CurrencyExchangeProviderType
{
    /// <summary>exchangeratesapi.io — EUR-based (see <see cref="ExchangeRateApiProvider"/>).</summary>
    ExchangeRatesApiIo,

    /// <summary>exchangerate-api.com — USD-based, 1,500 free calls/month (see <see cref="ExchangeRateApiComProvider"/>).</summary>
    ExchangeRateApiCom
}
