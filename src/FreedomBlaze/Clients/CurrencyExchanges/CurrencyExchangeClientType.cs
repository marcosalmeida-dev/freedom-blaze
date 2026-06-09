namespace FreedomBlaze.Clients.CurrencyExchanges;

/// <summary>
/// Selects which fiat currency exchange-rate API supplies conversion rates. Used as the DI key for the
/// keyed <see cref="FreedomBlaze.Interfaces.ICurrencyExchangeRateClient"/> registrations and chosen at
/// runtime via the "CurrencyExchange:Provider" configuration value.
/// </summary>
public enum CurrencyExchangeClientType
{
    /// <summary>exchangeratesapi.io — EUR-based (see <see cref="ExchangeRatesApiIoClient"/>).</summary>
    ExchangeRatesApiIo,

    /// <summary>exchangerate-api.com — USD-based, 1,500 free calls/month (see <see cref="ExchangeRateApiComClient"/>).</summary>
    ExchangeRateApiCom
}
