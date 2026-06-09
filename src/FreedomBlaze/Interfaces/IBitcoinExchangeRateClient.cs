using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

/// <summary>
/// A single Bitcoin exchange data source. Each implementation fetches the current BTC/USD rate from
/// one exchange API and either returns it or throws <see cref="Exceptions.ExchangeIntegrationException"/>.
/// </summary>
public interface IBitcoinExchangeRateClient
{
    /// <summary>The exchange's display name, also used as its named <see cref="HttpClient"/> key.</summary>
    string ExchangeName { get; }

    Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken);
}
