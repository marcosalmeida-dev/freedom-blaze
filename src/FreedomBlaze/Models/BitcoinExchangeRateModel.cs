namespace FreedomBlaze.Models;

public class BitcoinExchangeRateModel
{
    public string ExchangeName { get; set; } = string.Empty;
    public decimal BitcoinRateInUSD { get; set; }

    /// <summary>Fiat conversion rates; set only on the aggregated result, null on an individual exchange's result.</summary>
    public CurrencyExchangeRateModel? CurrencyExchangeRate { get; set; }
}
