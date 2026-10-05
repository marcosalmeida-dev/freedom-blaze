using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace FreedomBlaze.Components.Pages;

public partial class TransactionTracker : IAsyncDisposable
{
    [Inject] private IBitcoinTrackingService TrackingService { get; set; } = default!;
    [Inject] private IBitcoinHistoricalPriceService HistoricalPriceService { get; set; } = default!;
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private CultureService CultureService { get; set; } = default!;
    [Inject] private IStringLocalizer<Resources.Localization> Localizer { get; set; } = default!;
    [Inject] private ILogger<TransactionTracker> Logger { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _priceOperation;
    private Task? _refreshLoop;
    private BitcoinTrackingResult? _result;
    private IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult> _historicalPrices = new Dictionary<DateOnly, BitcoinHistoricalPriceResult>();
    private IReadOnlyList<string> _transactionContext = [];
    private Currency _selectedCurrency = CurrencyModel.CurrencyListStatic[0];
    private string _query = string.Empty;
    private string? _error;
    private bool _loading;
    private bool _loadingMore;
    private bool _loadingPrices;
    private bool _autoRefresh = true;
    private bool _walletInput;
    private bool _browsingHistory;
    private bool _disposed;
    private int _operationVersion;
    private int _priceVersion;

    private decimal? FiatPricePerBitcoin
    {
        get
        {
            var rates = AppState.BitcoinExchangeRate;
            if (rates is null || rates.BitcoinRateInUSD <= 0)
                return null;
            var factor = _selectedCurrency.Value == "USD" ? 1m
                : rates.CurrencyExchangeRate?.Rates.FirstOrDefault(rate => rate.Currency == _selectedCurrency.Value)?.Rate;
            return factor is > 0 ? rates.BitcoinRateInUSD * factor : null;
        }
    }

    protected override void OnInitialized()
    {
        _selectedCurrency = CurrencyModel.CurrencyListStatic.FirstOrDefault(currency =>
            currency.CultureName == CultureService.CurrentCulture.Name) ?? CurrencyModel.CurrencyListStatic[0];
        AppState.OnChange += OnRatesChangedAsync;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;
        _refreshLoop = RefreshLoopAsync();
        try
        {
            await AppState.EnsureRatesAsync(_lifetime.Token);
            if (!_disposed)
                StateHasChanged();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task OnRatesChangedAsync()
    {
        if (!_disposed)
            await InvokeAsync(() => { if (!_disposed) StateHasChanged(); });
    }

    private async Task SelectCurrency(string value)
    {
        var selected = CurrencyModel.CurrencyListStatic.FirstOrDefault(currency => currency.Value == value);
        if (selected is null || selected.Value == _selectedCurrency.Value)
            return;
        _selectedCurrency = selected;
        CancelPrices(clear: true);
        await FetchPricesAsync();
    }

    private Task SearchAsync()
    {
        _transactionContext = [];
        return LookupAsync(_query, clearResult: true);
    }

    private void SetQuery(string? value)
    {
        value ??= string.Empty;
        var identifiers = value.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (identifiers.Length > 1)
            _walletInput = true;
        _query = _walletInput ? value : value.Trim('\r', '\n');
    }

    private void ToggleWalletInput() => _walletInput = !_walletInput;

    private async Task OnQueryKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && !args.ShiftKey && !_walletInput && !_loading && !_loadingMore
            && !string.IsNullOrWhiteSpace(_query))
            await SearchAsync();
    }

    private Task RefreshAsync() => _result is null ? Task.CompletedTask : LookupAsync(_result.Query, clearResult: false);

    private async Task SelectTransactionAsync(string txId)
    {
        _transactionContext = _result?.TrackedAddresses ?? [];
        _query = txId;
        _walletInput = false;
        await LookupAsync(txId, clearResult: true);
    }

    private Task BackToWalletAsync()
    {
        _query = string.Join('\n', _transactionContext);
        _walletInput = _transactionContext.Count > 1;
        return SearchAsync();
    }

    private async Task LookupAsync(string query, bool clearResult)
    {
        if (_disposed || _loadingMore)
            return;

        _operation?.Cancel();
        _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _operation.Token;
        var version = ++_operationVersion;
        _loading = true;
        _error = null;
        if (clearResult)
        {
            _result = null;
            _browsingHistory = false;
            CancelPrices(clear: true);
        }

        try
        {
            var identifiers = query.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = identifiers.Length > 1
                ? await TrackingService.LookupWalletAsync(identifiers, token)
                : await TrackingService.LookupAsync(query.Trim(), token);
            if (result.Transaction is { } transaction && _transactionContext.Count > 0)
                result = result with
                {
                    TrackedAddresses = _transactionContext,
                    Transaction = BitcoinWalletAccounting.ApplyContext(transaction, _transactionContext),
                };
            if (!_disposed && version == _operationVersion)
            {
                _result = result;
                _browsingHistory = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (BitcoinTrackingException ex)
        {
            if (!_disposed && version == _operationVersion)
                _error = Localizer[$"Tracker.Error.{ex.Error}"].Value;
        }
        catch (Exception ex)
        {
            // Do not log the exception message/URI: it may contain the searched identifier.
            Logger.LogWarning("Bitcoin tracker lookup failed ({ExceptionType}).", ex.GetType().Name);
            if (!_disposed && version == _operationVersion)
                _error = Localizer["Tracker.Error.Unavailable"].Value;
        }
        finally
        {
            if (!_disposed && version == _operationVersion)
                _loading = false;
        }
        if (!_disposed && version == _operationVersion && _result is not null)
        {
            StateHasChanged();
            await FetchPricesAsync();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_disposed || _loading || _loadingMore || _result?.NextCursor is null)
            return;
        _loadingMore = true;
        _error = null;
        var current = _result;
        try
        {
            var page = current.Kind == BitcoinTrackingKind.Wallet
                ? await TrackingService.GetWalletTransactionsAsync(current.TrackedAddresses, current.NextCursor, _lifetime.Token)
                : await TrackingService.GetAddressTransactionsAsync(current.Query, current.NextCursor, _lifetime.Token);
            if (!_disposed)
            {
                _result = current with
                {
                    Transactions = current.Transactions.Concat(page.Transactions).DistinctBy(transaction => transaction.TxId).ToArray(),
                    NextCursor = page.NextCursor
                };
                // Keep a stable history while reading older pages. A refresh returns to the newest page.
                _autoRefresh = false;
                _browsingHistory = true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (BitcoinTrackingException ex)
        {
            _error = Localizer[ex.Error == BitcoinTrackingError.InvalidInput
                ? "Tracker.HistoryExpired" : $"Tracker.Error.{ex.Error}"].Value;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Bitcoin tracker history failed ({ExceptionType}).", ex.GetType().Name);
            _error = Localizer["Tracker.Error.Unavailable"].Value;
        }
        finally { if (!_disposed) _loadingMore = false; }
        if (!_disposed)
        {
            StateHasChanged();
            await FetchPricesAsync();
        }
    }

    private void CancelPrices(bool clear)
    {
        _priceOperation?.Cancel();
        _priceOperation?.Dispose();
        _priceOperation = null;
        ++_priceVersion;
        _loadingPrices = false;
        if (clear)
            _historicalPrices = new Dictionary<DateOnly, BitcoinHistoricalPriceResult>();
    }

    private async Task FetchPricesAsync()
    {
        if (_disposed || _result is null)
            return;
        CancelPrices(clear: false);
        _priceOperation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _priceOperation.Token;
        var version = _priceVersion;
        var currency = _selectedCurrency.Value;
        var transactions = _result.Transaction is { } transaction ? [transaction] : _result.Transactions;
        var dates = transactions.Where(tx => tx.Confirmed && tx.BlockTime.HasValue)
            .Select(tx => DateOnly.FromDateTime(tx.BlockTime!.Value.UtcDateTime)).Distinct().OrderDescending().ToArray();
        var prices = _historicalPrices.Where(pair => dates.Contains(pair.Key) && pair.Value.Currency == currency)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var missing = dates.Where(date => !prices.TryGetValue(date, out var price)
            || price.Status != BitcoinHistoricalPriceStatus.Available).ToArray();
        _loadingPrices = missing.Length > 0;
        try
        {
            // Bound each batch even when a reader has loaded a long history. Blockchain results are
            // already visible; historical-provider failures never erase a successful lookup.
            foreach (var batch in missing.Chunk(25))
            {
                var results = await Task.WhenAll(batch.Select(async date =>
                {
                    try { return await HistoricalPriceService.GetDailyPriceAsync(date, currency, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Historical Bitcoin price lookup failed ({ExceptionType}).", ex.GetType().Name);
                        return new BitcoinHistoricalPriceResult
                        {
                            Date = date, Currency = currency, Status = BitcoinHistoricalPriceStatus.Unavailable,
                            RetrievedAt = DateTimeOffset.UtcNow,
                        };
                    }
                }));
                if (_disposed || version != _priceVersion)
                    return;
                foreach (var result in results)
                    if (result.Currency == currency && batch.Contains(result.Date))
                        prices[result.Date] = result;
                _historicalPrices = new Dictionary<DateOnly, BitcoinHistoricalPriceResult>(prices);
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!_disposed && version == _priceVersion)
                _loadingPrices = false;
        }
    }

    private async Task SetAutoRefreshAsync(ChangeEventArgs args)
    {
        _autoRefresh = args.Value is true;
        if (_autoRefresh && _browsingHistory)
            await RefreshAsync();
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await InvokeAsync(async () =>
                {
                    if (!_disposed && _autoRefresh && _result is not null && !_loading && !_loadingMore)
                    {
                        await RefreshAsync();
                        if (!_disposed)
                            StateHasChanged();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("g", CultureService.CurrentCulture) + " UTC";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        AppState.OnChange -= OnRatesChangedAsync;
        await _lifetime.CancelAsync();
        if (_refreshLoop is not null)
            await _refreshLoop;
        _operation?.Dispose();
        _priceOperation?.Dispose();
        _lifetime.Dispose();
    }
}
