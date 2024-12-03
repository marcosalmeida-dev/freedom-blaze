using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces
{
    public interface ICurrencyExchangeProvider
    {
        Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken);
    }
}
