namespace FreedomBlaze.Models.BitcoinTracking;

public enum BitcoinHistoricalPriceStatus
{
    Available, DayNotComplete, NoData, RateLimited, Unavailable, UnsupportedCurrency,
}

/// <summary>
/// Coinbase Exchange's BTC-USD closing trade for a completed UTC day, converted using historical
/// daily FX. This is a daily estimate, not a block-time price or a traded BTC/local-currency close.
/// Missing quotes retain null prices; they must never be presented as zero.
/// </summary>
public sealed record BitcoinHistoricalPriceResult
{
    public required DateOnly Date { get; init; }
    public required string Currency { get; init; }
    public BitcoinHistoricalPriceStatus Status { get; init; }
    public decimal? PricePerBitcoin { get; init; }
    public decimal? BitcoinUsdClose { get; init; }
    public decimal? UsdToCurrencyRate { get; init; }
    /// <summary>The provider's FX observation date, which can precede the requested day on holidays.</summary>
    public DateOnly? FxDate { get; init; }
    public DateTimeOffset RetrievedAt { get; init; }
    public string BitcoinSource => "Coinbase Exchange BTC-USD UTC daily close";
    public string? FxSource => Currency == "USD" ? null : "Frankfurter historical daily reference FX";
}
