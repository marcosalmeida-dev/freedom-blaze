using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

/// <summary>
/// A fiat currency exchange-rate data source. Returns the latest conversion rates (USD-based) for the
/// app's supported currencies.
/// </summary>
public interface ICurrencyExchangeRateClient
{
    Task<CurrencyExchangeRateModel> GetCurrencyRateAsync(CancellationToken cancellationToken);
}
