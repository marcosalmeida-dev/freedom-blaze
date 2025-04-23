using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

public interface IExchangeRateProvider
{
    public string ExchangeName { get; }
    public Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken);
}
