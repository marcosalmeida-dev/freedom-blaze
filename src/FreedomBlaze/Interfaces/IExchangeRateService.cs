using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

/// <summary>
/// Aggregates the individual Bitcoin exchange clients into a single averaged BTC/USD rate (together
/// with fiat conversion rates) and exposes the per-exchange availability captured during the most
/// recent aggregation.
/// </summary>
public interface IExchangeRateService
{
    /// <summary>Per-exchange availability recorded by the last successful aggregation.</summary>
    List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; }

    /// <summary>The averaged BTC rate with fiat conversion, or <c>null</c> when no source succeeded.</summary>
    Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken);
}
