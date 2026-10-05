namespace FreedomBlaze.Models.BitcoinTracking;

public static class BitcoinWeightedAverageCalculator
{
    /// <summary>
    /// Computes received/spent averages independently from gross tracked satoshi amounts. Pending
    /// transactions and unfinished UTC days are excluded. Missing quotes or input amounts are never
    /// treated as zero prices or zero spending. The caller supplies full confirmed provider totals.
    /// </summary>
    public static BitcoinWeightedAverageSummary Calculate(IEnumerable<BitcoinTransaction> transactions,
        IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult> quotes, string currency,
        decimal confirmedReceivedSats, decimal confirmedSpentSats, DateOnly utcToday)
    {
        var normalizedCurrency = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (transactions is null || quotes is null || normalizedCurrency.Length == 0)
            return UnavailableSummary(normalizedCurrency, confirmedReceivedSats, confirmedSpentSats);
        var confirmed = new Dictionary<string, BitcoinTransaction>(StringComparer.OrdinalIgnoreCase);
        foreach (var transaction in transactions)
        {
            if (transaction is null || string.IsNullOrWhiteSpace(transaction.TxId))
                return UnavailableSummary(normalizedCurrency, confirmedReceivedSats, confirmedSpentSats);
            // Filter first: an earlier pending representation must not hide a confirmed copy.
            if (!transaction.Confirmed) continue;
            if (!confirmed.TryGetValue(transaction.TxId, out var existing))
                confirmed.Add(transaction.TxId, transaction);
            else
                confirmed[transaction.TxId] = existing with
                {
                    BlockTime = existing.BlockTime ?? transaction.BlockTime,
                    TrackedReceivedSats = existing.TrackedReceivedSats ?? transaction.TrackedReceivedSats,
                    TrackedSpentSats = existing.TrackedSpentSats ?? transaction.TrackedSpentSats,
                };
        }
        return new BitcoinWeightedAverageSummary
        {
            Currency = normalizedCurrency,
            Received = CalculateCoverage(confirmed.Values, quotes, normalizedCurrency, confirmedReceivedSats,
                utcToday, transaction => transaction.TrackedReceivedSats),
            Spent = CalculateCoverage(confirmed.Values, quotes, normalizedCurrency, confirmedSpentSats,
                utcToday, transaction => transaction.TrackedSpentSats),
        };
    }

    private static BitcoinWeightedAverageCoverage CalculateCoverage(IEnumerable<BitcoinTransaction> transactions,
        IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult> quotes, string currency, decimal confirmedTotal,
        DateOnly utcToday, Func<BitcoinTransaction, long?> amount)
    {
        if (confirmedTotal < 0 || decimal.Truncate(confirmedTotal) != confirmedTotal)
            return Unavailable(confirmedTotal);
        try
        {
            decimal knownLoaded = 0, priced = 0, weightedPrices = 0;
            var eligibleCount = 0;
            var pricedCount = 0;
            var unknownCount = 0;
            foreach (var transaction in transactions)
            {
                if (amount(transaction) is not { } sats)
                {
                    unknownCount++;
                    continue;
                }
                if (sats < 0) return Unavailable(confirmedTotal);
                knownLoaded = checked(knownLoaded + sats);
                if (sats == 0 || transaction.BlockTime is not { } time) continue;
                var date = DateOnly.FromDateTime(time.UtcDateTime);
                if (date >= utcToday) continue;
                eligibleCount++;
                if (!quotes.TryGetValue(date, out var quote) || quote is null || quote.Date != date
                    || !string.Equals(quote.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase)
                    || quote.Status != BitcoinHistoricalPriceStatus.Available || quote.PricePerBitcoin is not > 0)
                    continue;
                priced = checked(priced + sats);
                weightedPrices = checked(weightedPrices + sats * quote.PricePerBitcoin.Value);
                pricedCount++;
            }
            // Provider totals are authoritative. A stale/mismatched history cannot turn into a
            // fabricated 100% figure or a supposedly complete lifetime mean.
            if (knownLoaded > confirmedTotal || priced > knownLoaded)
                return Unavailable(confirmedTotal);
            return new BitcoinWeightedAverageCoverage
            {
                AveragePricePerBitcoin = priced > 0 ? weightedPrices / priced : null,
                PricedSats = priced,
                KnownLoadedSats = knownLoaded,
                ConfirmedTotalSats = confirmedTotal,
                CoveragePercent = confirmedTotal > 0 ? priced / confirmedTotal * 100m : null,
                PricedTransactionCount = pricedCount,
                EligibleTransactionCount = eligibleCount,
                UnknownAmountTransactionCount = unknownCount,
                CoverageComplete = priced > 0 && priced == confirmedTotal && unknownCount == 0,
            };
        }
        catch (OverflowException)
        {
            // Do not expose an average from whichever terms happened to precede the overflow.
            return Unavailable(confirmedTotal);
        }
    }

    private static BitcoinWeightedAverageCoverage Unavailable(decimal confirmedTotal) => new()
    {
        ConfirmedTotalSats = confirmedTotal,
        CalculationUnavailable = true,
    };

    private static BitcoinWeightedAverageSummary UnavailableSummary(string currency, decimal received, decimal spent) => new()
    {
        Currency = currency, Received = Unavailable(received), Spent = Unavailable(spent),
    };
}
