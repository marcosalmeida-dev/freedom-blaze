namespace FreedomBlaze.Models.BitcoinTracking;

public sealed record BitcoinWeightedAverageSummary
{
    public required string Currency { get; init; }
    public required BitcoinWeightedAverageCoverage Received { get; init; }
    public required BitcoinWeightedAverageCoverage Spent { get; init; }
}

/// <summary>
/// The amount-weighted historical daily price for confirmed gross flows with usable quotes.
/// Coverage is measured against complete confirmed provider totals, not just the loaded page.
/// </summary>
public sealed record BitcoinWeightedAverageCoverage
{
    public decimal? AveragePricePerBitcoin { get; init; }
    public decimal PricedSats { get; init; }
    public decimal KnownLoadedSats { get; init; }
    public decimal ConfirmedTotalSats { get; init; }
    public decimal? CoveragePercent { get; init; }
    public int PricedTransactionCount { get; init; }
    public int EligibleTransactionCount { get; init; }
    public int UnknownAmountTransactionCount { get; init; }
    public bool CoverageComplete { get; init; }
    public bool CalculationUnavailable { get; init; }
}
