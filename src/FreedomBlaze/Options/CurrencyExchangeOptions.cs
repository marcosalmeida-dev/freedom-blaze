using FreedomBlaze.WebClients.CurrencyExchanges;

namespace FreedomBlaze.Options;

/// <summary>
/// Configuration for fiat currency exchange-rate retrieval, bound from the "CurrencyExchange" section.
/// Switch the active provider by changing <see cref="Provider"/> — no code changes required.
/// </summary>
public class CurrencyExchangeOptions
{
    public const string Section = "CurrencyExchange";

    /// <summary>The active currency exchange-rate provider. Defaults to exchangerate-api.com.</summary>
    public CurrencyExchangeProviderType Provider { get; set; } = CurrencyExchangeProviderType.ExchangeRateApiCom;

    /// <summary>How long fetched rates are cached before another upstream API call is made. Defaults to 1 hour.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
}
