using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

public interface IExchangeRateProvider
{
    Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken);
}
