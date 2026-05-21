namespace FreedomBlaze.Models;

public class BitcoinExchangeRateModel
{
    public string ExchangeName { get; set; }
    public decimal BitcoinRateInUSD { get; set; }
    public CurrencyExchangeRateModel CurrencyExchangeRate { get; set; }
}
