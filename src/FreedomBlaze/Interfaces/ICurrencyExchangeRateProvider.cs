using FreedomBlaze.Models;
using FreedomBlaze.WebClients.CurrencyExchanges.ExchangeRateApi;

namespace FreedomBlaze.Interfaces
{
    public interface ICurrencyExchangeProvider
    {
        Task<CurrencyExchangeRateModel> GetCurrencyRate(CancellationToken cancellationToken);
    }
}