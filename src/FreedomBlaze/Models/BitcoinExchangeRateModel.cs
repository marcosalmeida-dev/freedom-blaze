namespace FreedomBlaze.Models;

public class BitcoinExchangeRateModel
{
    public string ExchangeName { get; set; } = string.Empty;
    public decimal BitcoinRateInUSD { get; set; }

    /// <summary>
    /// How many exchanges contributed to <see cref="BitcoinRateInUSD"/>; set only on the aggregated
    /// result. Callers that price real payments can demand more than one independent source.
    /// </summary>
    public int SourceCount { get; set; }

    /// <summary>Fiat conversion rates; set only on the aggregated result, null on an individual exchange's result.</summary>
    public CurrencyExchangeRateModel? CurrencyExchangeRate { get; set; }
}
