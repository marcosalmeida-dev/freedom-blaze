namespace FreedomBlaze.Models;

public class CurrencyExchangeRateModel
{
    public DateTime? Date { get; set; }
    public List<CurrencyRate> Rates { get; set; } = [];

    /// <summary>
    /// True for the development placeholders served when no provider key is configured. They carry a
    /// fresh <see cref="Date"/>, so only this flag tells them apart from market data; a payment must
    /// never be priced with them.
    /// </summary>
    public bool IsSimulated { get; set; }

    // Aggregated snapshots must not retain fiat rates past their own cache expiration.
    internal DateTimeOffset? CacheExpiresAt { get; set; }

    public CurrencyRate this[string currency] => FindCurrencyIndex(currency);

    private CurrencyRate FindCurrencyIndex(string currencyValue)
    {
        for (int j = 0; j < Rates.Count; j++)
        {
            if (Rates[j].Currency == currencyValue)
            {
                return Rates[j];
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(currencyValue),
            $"Currency not found: {currencyValue}.");
    }
}

public class CurrencyRate
{
    public string Currency { get; set; } = string.Empty;
    public decimal Rate { get; set; }
}
