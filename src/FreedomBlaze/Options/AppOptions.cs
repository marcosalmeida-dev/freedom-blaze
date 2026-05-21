using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Options;

public class AppOptions
{
    public const string Section = "App";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    public string? CurrencyExchangeRateApi { get; set; }
}
