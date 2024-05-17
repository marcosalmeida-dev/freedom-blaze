using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Models;

public class BitcoinExchangeRateModel
{
    [Required]
    public string Ticker { get; set; } = "";

    [Required]
    public decimal Rate { get; set; }
}
