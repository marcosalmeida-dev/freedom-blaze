using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

public interface IExchangeRateProvider
{
    string ExchangeName { get; }
    Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken);
}
