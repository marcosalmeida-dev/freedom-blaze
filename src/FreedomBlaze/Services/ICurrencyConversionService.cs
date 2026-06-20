using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>
/// Converts an amount between any pair of supported fiat currencies and Bitcoin units (BTC/SATS)
/// using the app's aggregated, cached exchange rates — the same source that powers the Sats Converter.
/// </summary>
public interface ICurrencyConversionService
{
    /// <summary>The codes accepted in conversion requests (fiat codes plus <c>BTC</c> and <c>SATS</c>).</summary>
    IReadOnlyCollection<string> SupportedCurrencies { get; }

    /// <summary>
    /// Converts <paramref name="request"/> and returns the result.
    /// </summary>
    /// <exception cref="Exceptions.ConversionException">
    /// Thrown for an unsupported currency code (client error) or when no live rate is available
    /// (server error).
    /// </exception>
    Task<ConversionResponse> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken);
}
