using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Models.Api;

/// <summary>
/// Request body for the public conversion endpoint. Converts <see cref="Amount"/> of
/// <see cref="From"/> into <see cref="To"/>. Codes are ISO 4217 fiat codes (e.g. <c>USD</c>,
/// <c>EUR</c>, <c>BRL</c>) or the Bitcoin units <c>BTC</c> and <c>SATS</c>; either side may be a
/// Bitcoin unit, mirroring the on-site Sats Converter.
/// </summary>
public sealed class ConversionRequest
{
    /// <summary>The amount of <see cref="From"/> to convert. Must be greater than zero.</summary>
    [Range(0.0, double.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
    public decimal Amount { get; set; }

    /// <summary>Source currency/unit code (e.g. <c>USD</c>, <c>BTC</c>, <c>SATS</c>).</summary>
    [Required(ErrorMessage = "The 'from' currency code is required.")]
    [StringLength(8, MinimumLength = 2)]
    public string From { get; set; } = string.Empty;

    /// <summary>Target currency/unit code (e.g. <c>SATS</c>, <c>BTC</c>, <c>EUR</c>).</summary>
    [Required(ErrorMessage = "The 'to' currency code is required.")]
    [StringLength(8, MinimumLength = 2)]
    public string To { get; set; } = string.Empty;
}
