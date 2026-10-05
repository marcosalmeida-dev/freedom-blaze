using System.Globalization;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace FreedomBlaze.Components.Pages;

public partial class BitcoinTrackingDetails
{
    [Inject] private IStringLocalizer<Resources.Localization> Localizer { get; set; } = default!;
    [Parameter, EditorRequired] public BitcoinTrackingResult Result { get; set; } = default!;
    [Parameter] public Currency SelectedCurrency { get; set; } = CurrencyModel.CurrencyListStatic[0];
    [Parameter] public decimal? FiatPricePerBitcoin { get; set; }
    [Parameter] public IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult> HistoricalPrices { get; set; } = new Dictionary<DateOnly, BitcoinHistoricalPriceResult>();
    [Parameter] public bool LoadingPrices { get; set; }
    [Parameter] public bool LoadingMore { get; set; }
    [Parameter] public EventCallback OnLoadMore { get; set; }
    [Parameter] public EventCallback<string> OnSelectTransaction { get; set; }

    private int _inputLimit = 25;
    private int _outputLimit = 25;
    private string? _displayedQuery;
    private BitcoinWeightedAverageSummary? _averages;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private DateOnly? FromDate => _fromDate is { } date ? DateOnly.FromDateTime(date) : null;
    private DateOnly? ToDate => _toDate is { } date ? DateOnly.FromDateTime(date) : null;
    private bool HasPeriod => FromDate.HasValue || ToDate.HasValue;
    private bool InvalidPeriod => FromDate.HasValue && ToDate.HasValue && FromDate > ToDate;
    private long ConfirmedBalance => Result.Wallet?.ConfirmedBalanceSats ?? Result.Address?.ConfirmedBalanceSats ?? 0;
    private long PendingChange => Result.Wallet?.PendingBalanceChangeSats ?? Result.Address?.PendingBalanceChangeSats ?? 0;
    private long TotalBalance => Result.Wallet?.TotalBalanceSats ?? Result.Address?.TotalBalanceSats ?? 0;
    private long TotalReceived => checked((Result.Wallet?.ConfirmedReceivedSats ?? Result.Address?.ConfirmedReceivedSats ?? 0)
        + (Result.Wallet?.PendingReceivedSats ?? Result.Address?.PendingReceivedSats ?? 0));
    private long TotalSpent => checked((Result.Wallet?.ConfirmedSpentSats ?? Result.Address?.ConfirmedSpentSats ?? 0)
        + (Result.Wallet?.PendingSpentSats ?? Result.Address?.PendingSpentSats ?? 0));

    private IEnumerable<BitcoinTransaction> HistoryTransactions => InvalidPeriod ? [] : Result.Transactions.Where(tx =>
        !HasPeriod || tx.Confirmed && tx.BlockTime is { } time
        && (!FromDate.HasValue || DateOnly.FromDateTime(time.UtcDateTime) >= FromDate.Value)
        && (!ToDate.HasValue || DateOnly.FromDateTime(time.UtcDateTime) <= ToDate.Value));

    protected override void OnParametersSet()
    {
        _averages = Result.Kind is BitcoinTrackingKind.Address or BitcoinTrackingKind.Wallet
            ? BitcoinWeightedAverageCalculator.Calculate(Result.Transactions, HistoricalPrices, SelectedCurrency.Value,
                Result.Wallet?.ConfirmedReceivedSats ?? Result.Address?.ConfirmedReceivedSats ?? 0,
                Result.Wallet?.ConfirmedSpentSats ?? Result.Address?.ConfirmedSpentSats ?? 0,
                DateOnly.FromDateTime(DateTime.UtcNow))
            : null;
        if (_displayedQuery == Result.Query)
            return;
        _displayedQuery = Result.Query;
        _inputLimit = _outputLimit = 25;
        ClearPeriod();
    }

    private void ClearPeriod() => _fromDate = _toDate = null;
    private static long? Net(BitcoinTransaction transaction) => transaction.TrackedNetSats ?? transaction.AddressNetSats;
    private static long? Incoming(BitcoinTransaction transaction) => Net(transaction) is { } net ? Math.Max(net, 0) : null;
    private static long? Outgoing(BitcoinTransaction transaction) => Net(transaction) is { } net ? checked(Math.Max(-net, 0)) : null;
    private static string FlowClass(long? value) => value > 0 ? "incoming-value" : value < 0 ? "outgoing-value" : "neutral-value";
    private static long? SumKnown(IEnumerable<long?> values)
    {
        long sum = 0;
        try
        {
            foreach (var value in values)
            {
                if (value is null)
                    return null;
                sum = checked(sum + value.Value);
            }
            return sum;
        }
        catch (OverflowException) { return null; }
    }

    private BitcoinHistoricalPriceResult? HistoricalQuote(BitcoinTransaction transaction)
    {
        if (!transaction.Confirmed || transaction.BlockTime is not { } time)
            return null;
        var date = DateOnly.FromDateTime(time.UtcDateTime);
        return HistoricalPrices.TryGetValue(date, out var quote) && quote.Date == date && quote.Currency == SelectedCurrency.Value
            ? quote : null;
    }

    private static bool IsAvailable(BitcoinHistoricalPriceResult? quote) =>
        quote is { Status: BitcoinHistoricalPriceStatus.Available, PricePerBitcoin: > 0 };

    private string HistoricalStatus(BitcoinTransaction transaction, BitcoinHistoricalPriceResult? quote)
    {
        var key = !transaction.Confirmed ? "Tracker.AwaitingConfirmation"
            : quote?.Status == BitcoinHistoricalPriceStatus.DayNotComplete ? "Tracker.DailyClosePending"
            : quote?.Status == BitcoinHistoricalPriceStatus.RateLimited ? "Tracker.HistoricalRateLimited"
            : quote is null && LoadingPrices ? "Tracker.HistoricalLoading"
            : "Tracker.HistoricalUnavailable";
        return Localizer[key].Value;
    }

    private static string FormatSats(long? sats, bool signed = false) => sats is { } value
        ? (signed && value > 0 ? "+" : string.Empty) + value.ToString("N0", DisplayCulture) : "—";
    private static string FormatOutgoing(long? sats) => sats is { } value
        ? (value > 0 ? "-" : string.Empty) + value.ToString("N0", DisplayCulture) : "—";
    private static string FormatBtc(long sats) => (sats / 100_000_000m).ToString("N8", DisplayCulture);
    private string FormatMoney(decimal value) => value.ToString("C", SelectedCurrency.CultureInfo);
    private string FormatAverage(BitcoinWeightedAverageCoverage average) => average.AveragePricePerBitcoin is { } value
        ? "≈ " + FormatMoney(value) : "—";
    private static string FormatAverageBtc(decimal sats) => (sats / 100_000_000m).ToString("N8", DisplayCulture);
    private string FormatFiat(long sats) => sats == 0 ? FormatMoney(0)
        : FiatPricePerBitcoin is > 0 ? "≈ " + FormatMoney(sats / 100_000_000m * FiatPricePerBitcoin.Value) : "—";
    private string FormatHistorical(long? sats, BitcoinTransaction transaction) =>
        sats is { } value && IsAvailable(HistoricalQuote(transaction))
            ? "≈ " + FormatMoney(value / 100_000_000m * HistoricalQuote(transaction)!.PricePerBitcoin!.Value) : "—";
    private string StatusLabel(BitcoinTransaction transaction) => transaction.Confirmed
        ? Localizer["Tracker.ConfirmationsValue", transaction.Confirmations.ToString("N0", DisplayCulture)].Value
        : Localizer["Tracker.Pending"].Value;
    private static long VirtualSize(BitcoinTransaction transaction) => (transaction.Weight + 3L) / 4;
    private static string FormatFeeRate(BitcoinTransaction transaction) => VirtualSize(transaction) > 0
        ? (transaction.FeeSats / (decimal)VirtualSize(transaction)).ToString("N2", DisplayCulture) : "—";
    private static string ShortId(string txId) => txId.Length > 24 ? txId[..12] + "…" + txId[^8..] : txId;
    private static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString("g", DisplayCulture) + " UTC";
    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
