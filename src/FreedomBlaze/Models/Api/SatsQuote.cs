namespace FreedomBlaze.Models.Api;

/// <summary>
/// Conditions a quote must meet before it may price a real payment. A display conversion can live
/// with a single exchange or yesterday's fiat rate; an invoice cannot, because the payer is charged
/// exactly what the quote says.
/// </summary>
/// <param name="MinimumSources">Independent BTC/USD sources the aggregated price must come from.</param>
/// <param name="MaxFiatRateAge">Oldest acceptable fiat (USD → currency) rate.</param>
public sealed record SatsQuoteRules(int MinimumSources, TimeSpan MaxFiatRateAge);

/// <summary>
/// An amount priced in satoshis, rounded <b>up</b> so the payee never receives less than the fiat
/// amount, together with the rates it was derived from so the figure can be audited later.
/// </summary>
public sealed record SatsQuote
{
    /// <summary>The amount that was priced, in <see cref="Currency"/>.</summary>
    public decimal Amount { get; init; }

    /// <summary>Normalised (upper-case) currency or Bitcoin unit code.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Whole satoshis to charge, rounded up.</summary>
    public long AmountSat { get; init; }

    /// <summary>Price of one bitcoin in <see cref="Currency"/> that the quote used.</summary>
    public decimal BitcoinPrice { get; init; }

    /// <summary>The aggregated BTC/USD price, or <c>null</c> when no market data was needed (BTC, SATS).</summary>
    public decimal? BitcoinPriceUsd { get; init; }

    /// <summary>How many exchanges the BTC/USD price came from (0 when no market data was needed).</summary>
    public int RateSources { get; init; }

    /// <summary>When the fiat rate was published; <c>null</c> when no fiat conversion was involved.</summary>
    public DateTimeOffset? FiatRateTimestamp { get; init; }

    /// <summary>When the quote was produced (UTC).</summary>
    public DateTimeOffset QuotedAt { get; init; }
}
