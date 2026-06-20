namespace FreedomBlaze.Models.Api;

/// <summary>
/// Result of a conversion. <see cref="Result"/> is rounded to the natural precision of
/// <see cref="To"/> (whole satoshis, 8 dp for BTC, 2 dp for fiat); <see cref="Rate"/> and
/// <see cref="BitcoinPriceUsd"/> are exposed so callers can audit or recompute the figure.
/// </summary>
public sealed class ConversionResponse
{
    /// <summary>The amount that was converted (echoed from the request).</summary>
    public decimal Amount { get; init; }

    /// <summary>Normalised (upper-case) source code.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Normalised (upper-case) target code.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>The converted amount in <see cref="To"/>.</summary>
    public decimal Result { get; init; }

    /// <summary>How many units of <see cref="To"/> one unit of <see cref="From"/> is worth.</summary>
    public decimal Rate { get; init; }

    /// <summary>The aggregated BTC/USD price the conversion was derived from.</summary>
    public decimal BitcoinPriceUsd { get; init; }

    /// <summary>When the underlying rates were produced (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }
}
