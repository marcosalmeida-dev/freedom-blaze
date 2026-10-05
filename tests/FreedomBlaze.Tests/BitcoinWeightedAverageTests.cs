using FreedomBlaze.Models.BitcoinTracking;

namespace FreedomBlaze.Tests;

public sealed class BitcoinWeightedAverageTests
{
    private static readonly DateOnly Day = new(2024, 1, 6);
    private static readonly DateOnly Today = new(2026, 10, 4);

    [Fact]
    public void GrossReceivedAndSpentAreWeightedIndependentlyInsteadOfUsingNetFlows()
    {
        var transactions = new[] { Transaction('a', Day, 100, 90), Transaction('b', Day.AddDays(1), 300, 10) };
        var prices = Quotes(Quote(Day, 10_000), Quote(Day.AddDays(1), 30_000));

        var result = Calculate(transactions, prices, 400, 100);

        Assert.Equal(25_000m, result.Received.AveragePricePerBitcoin);
        Assert.Equal(12_000m, result.Spent.AveragePricePerBitcoin);
        Assert.Equal(400m, result.Received.PricedSats);
        Assert.Equal(100m, result.Spent.PricedSats);
        Assert.Equal(100m, result.Received.CoveragePercent);
        Assert.True(result.Received.CoverageComplete);
        Assert.True(result.Spent.CoverageComplete);
        Assert.Equal(2, result.Received.PricedTransactionCount);
        Assert.Equal(2, result.Spent.EligibleTransactionCount);
    }

    [Fact]
    public void MissingQuotesAndUnloadedHistoryRemainPartialAgainstFullProviderTotals()
    {
        var transactions = new[] { Transaction('a', Day, 100, 25), Transaction('b', Day.AddDays(1), 300, 75) };
        var prices = Quotes(Quote(Day, 10_000), Quote(Day.AddDays(1), 99_999) with { Status = BitcoinHistoricalPriceStatus.Unavailable });

        var result = Calculate(transactions, prices, 1_000, 500);

        Assert.Equal(10_000m, result.Received.AveragePricePerBitcoin);
        Assert.Equal(400m, result.Received.KnownLoadedSats);
        Assert.Equal(100m, result.Received.PricedSats);
        Assert.Equal(10m, result.Received.CoveragePercent);
        Assert.Equal(5m, result.Spent.CoveragePercent);
        Assert.Equal(2, result.Received.EligibleTransactionCount);
        Assert.Equal(1, result.Received.PricedTransactionCount);
        Assert.False(result.Received.CoverageComplete);
        Assert.False(result.Received.CalculationUnavailable);
    }

    [Fact]
    public void PendingTransactionsAndIncompleteOrFutureUtcDaysDoNotSupplyPrices()
    {
        var transactions = new[]
        {
            Transaction('a', Day, 100, 25),
            Transaction('b', Day, 9_000, 9_000) with { Confirmed = false },
            Transaction('c', Today, 200, 50),
            Transaction('d', Today.AddDays(1), 300, 75),
        };
        var prices = Quotes(Quote(Day, 10_000), Quote(Today, 999_999), Quote(Today.AddDays(1), 888_888));

        var result = Calculate(transactions, prices, 600, 150);

        Assert.Equal(10_000m, result.Received.AveragePricePerBitcoin);
        Assert.Equal(600m, result.Received.KnownLoadedSats);
        Assert.Equal(100m, result.Received.PricedSats);
        Assert.Equal(1, result.Received.EligibleTransactionCount);
        Assert.Equal(1, result.Received.PricedTransactionCount);
        Assert.False(result.Received.CoverageComplete);
    }

    [Fact]
    public void MissingPreviousOutputDoesNotEraseKnownReceivedAmountsOrAssumeZeroSpending()
    {
        var transactions = new[] { Transaction('a', Day, 100, null), Transaction('b', Day.AddDays(1), 300, 50) };
        var prices = Quotes(Quote(Day, 10_000), Quote(Day.AddDays(1), 30_000));

        var result = Calculate(transactions, prices, 400, 100);

        Assert.Equal(25_000m, result.Received.AveragePricePerBitcoin);
        Assert.True(result.Received.CoverageComplete);
        Assert.Equal(30_000m, result.Spent.AveragePricePerBitcoin);
        Assert.Equal(50m, result.Spent.KnownLoadedSats);
        Assert.Equal(50m, result.Spent.CoveragePercent);
        Assert.Equal(1, result.Spent.UnknownAmountTransactionCount);
        Assert.False(result.Spent.CoverageComplete);
    }

    [Fact]
    public void DuplicateTxidsPreferConfirmedRepresentationsAndFillKnownFieldsWithoutDoubleCounting()
    {
        var confirmed = Transaction('a', Day, 100, 25);
        var incomplete = confirmed with { TrackedSpentSats = null, BlockTime = null };
        var transactions = new[]
        {
            confirmed with { Confirmed = false, TrackedReceivedSats = 9_999 },
            incomplete,
            confirmed with { TxId = confirmed.TxId.ToUpperInvariant() },
            confirmed,
        };

        var result = Calculate(transactions, Quotes(Quote(Day, 10_000)), 100, 25);

        Assert.Equal(100m, result.Received.PricedSats);
        Assert.Equal(25m, result.Spent.PricedSats);
        Assert.Equal(1, result.Received.PricedTransactionCount);
        Assert.Equal(1, result.Spent.PricedTransactionCount);
        Assert.Equal(0, result.Spent.UnknownAmountTransactionCount);
        Assert.True(result.Spent.CoverageComplete);
    }

    [Fact]
    public void UnknownSpendingIsNotDeclaredCompleteEvenWhenOtherKnownAmountsMatchTheProviderTotal()
    {
        var transactions = new[] { Transaction('a', Day, 100, null), Transaction('b', Day.AddDays(1), 300, 50) };

        var result = Calculate(transactions, Quotes(Quote(Day, 10_000), Quote(Day.AddDays(1), 30_000)), 400, 50);

        Assert.Equal(100m, result.Spent.CoveragePercent);
        Assert.Equal(1, result.Spent.UnknownAmountTransactionCount);
        Assert.False(result.Spent.CoverageComplete);
        Assert.False(result.Spent.CalculationUnavailable);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("currency")]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("null")]
    [InlineData("status")]
    public void AQuoteMustMatchTheCompletedUtcDayCurrencyStatusAndPositivePrice(string failure)
    {
        var quote = Quote(Day, 10_000);
        quote = failure switch
        {
            "date" => quote with { Date = Day.AddDays(1) },
            "currency" => quote with { Currency = "BRL" },
            "zero" => quote with { PricePerBitcoin = 0 },
            "negative" => quote with { PricePerBitcoin = -1 },
            "null" => quote with { PricePerBitcoin = null },
            "status" => quote with { Status = BitcoinHistoricalPriceStatus.DayNotComplete },
            _ => quote,
        };
        var prices = failure == "missing" ? new Dictionary<DateOnly, BitcoinHistoricalPriceResult>()
            : new Dictionary<DateOnly, BitcoinHistoricalPriceResult> { [Day] = quote };

        var result = Calculate([Transaction('a', Day, 100, 25)], prices, 100, 25);

        Assert.Null(result.Received.AveragePricePerBitcoin);
        Assert.Equal(0m, result.Received.PricedSats);
        Assert.Equal(100m, result.Received.KnownLoadedSats);
        Assert.Equal(0m, result.Received.CoveragePercent);
        Assert.False(result.Received.CoverageComplete);
        Assert.False(result.Received.CalculationUnavailable);
    }

    [Fact]
    public void QuoteDayUsesUtcAndCurrencyIsNormalized()
    {
        var transaction = Transaction('a', Day, 100, 25) with
        {
            BlockTime = new DateTimeOffset(2024, 1, 5, 23, 30, 0, TimeSpan.FromHours(-3)),
        };

        var result = BitcoinWeightedAverageCalculator.Calculate([transaction], Quotes(Quote(Day, 49_000) with { Currency = " usd " }),
            " usd ", 100, 25, Today);

        Assert.Equal("USD", result.Currency);
        Assert.Equal(49_000m, result.Received.AveragePricePerBitcoin);
        Assert.True(result.Received.CoverageComplete);
    }

    [Fact]
    public void ZeroFlowsAndMissingConfirmationDatesDoNotCreateAnAverageOrFalseCompleteCoverage()
    {
        var zero = Calculate([Transaction('a', Day, 0, 0)], Quotes(Quote(Day, 10_000)), 0, 0);
        var undated = Calculate([Transaction('a', Day, 100, 25) with { BlockTime = null }], Quotes(Quote(Day, 10_000)), 100, 25);

        Assert.Null(zero.Received.AveragePricePerBitcoin);
        Assert.Null(zero.Spent.CoveragePercent);
        Assert.False(zero.Received.CoverageComplete);
        Assert.False(zero.Received.CalculationUnavailable);
        Assert.Null(undated.Received.AveragePricePerBitcoin);
        Assert.Equal(100m, undated.Received.KnownLoadedSats);
        Assert.Equal(0, undated.Received.EligibleTransactionCount);
    }

    [Fact]
    public void ProviderTotalsSmallerThanLoadedAmountsAreUnavailableInsteadOfClampedCoverage()
    {
        var result = Calculate([Transaction('a', Day, 200, 25)], Quotes(Quote(Day, 10_000)), 100, 25);

        Assert.True(result.Received.CalculationUnavailable);
        Assert.Null(result.Received.AveragePricePerBitcoin);
        Assert.Null(result.Received.CoveragePercent);
        Assert.False(result.Received.CoverageComplete);
        Assert.False(result.Spent.CalculationUnavailable);
        Assert.Equal(10_000m, result.Spent.AveragePricePerBitcoin);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void NegativeAmountsOrDenominatorsAreUnavailable(long received, long expected)
    {
        var result = Calculate([Transaction('a', Day, received, 0)], Quotes(Quote(Day, 10_000)), expected, 0);

        Assert.True(result.Received.CalculationUnavailable);
        Assert.Null(result.Received.AveragePricePerBitcoin);
    }

    [Fact]
    public void DecimalOverflowDoesNotReturnAnAverageOfOnlyTheEarlierTransactions()
    {
        var transactions = new[] { Transaction('a', Day, 1, 1), Transaction('b', Day.AddDays(1), 2, 0) };
        var prices = Quotes(Quote(Day, 10_000), Quote(Day.AddDays(1), decimal.MaxValue));

        var result = Calculate(transactions, prices, 3, 1);

        Assert.True(result.Received.CalculationUnavailable);
        Assert.Null(result.Received.AveragePricePerBitcoin);
        Assert.Null(result.Received.CoveragePercent);
        Assert.Equal(0m, result.Received.PricedSats);
        Assert.False(result.Spent.CalculationUnavailable);
        Assert.Equal(10_000m, result.Spent.AveragePricePerBitcoin);
    }

    [Fact]
    public void CumulativeLoadedSatoshisUseDecimalEvenWhenTheyExceedALong()
    {
        var transactions = new[] { Transaction('a', Day, long.MaxValue, 0), Transaction('b', Day, long.MaxValue, 0) };
        var expected = long.MaxValue * 2m;

        var result = Calculate(transactions, Quotes(Quote(Day, 1)), expected, 0);

        Assert.Equal(expected, result.Received.KnownLoadedSats);
        Assert.Equal(expected, result.Received.PricedSats);
        Assert.Equal(1m, result.Received.AveragePricePerBitcoin);
        Assert.True(result.Received.CoverageComplete);
    }

    private static BitcoinWeightedAverageSummary Calculate(IEnumerable<BitcoinTransaction> transactions,
        IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult> prices, decimal received, decimal spent) =>
        BitcoinWeightedAverageCalculator.Calculate(transactions, prices, "USD", received, spent, Today);

    private static BitcoinTransaction Transaction(char id, DateOnly day, long? received, long? spent) => new()
    {
        TxId = new string(id, 64), Confirmed = true,
        BlockTime = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)),
        TrackedReceivedSats = received, TrackedSpentSats = spent,
        TrackedNetSats = received is { } incoming && spent is { } outgoing ? incoming - outgoing : null,
    };

    private static BitcoinHistoricalPriceResult Quote(DateOnly day, decimal price) => new()
    {
        Date = day, Currency = "USD", Status = BitcoinHistoricalPriceStatus.Available, PricePerBitcoin = price,
    };

    private static Dictionary<DateOnly, BitcoinHistoricalPriceResult> Quotes(params BitcoinHistoricalPriceResult[] quotes) =>
        quotes.ToDictionary(quote => quote.Date);
}
