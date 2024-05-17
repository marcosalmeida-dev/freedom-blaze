namespace FreedomBlaze.Models;

public class CurrencyExchangeRateModel
{
    public string Currency {  get; set; }
    public DateTime? Date { get; set; }
    public List<CurrencyRate> Rates { get; set; } = new List<CurrencyRate>();

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
    public string Currency { get; set; }
    public decimal Rate { get; set; }
}
