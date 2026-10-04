using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace FreedomBlaze.Components.Pages;

public partial class TransactionTracker : IAsyncDisposable
{
    [Inject] private IBitcoinTrackingService TrackingService { get; set; } = default!;
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private CultureService CultureService { get; set; } = default!;
    [Inject] private IStringLocalizer<Resources.Localization> Localizer { get; set; } = default!;
    [Inject] private ILogger<TransactionTracker> Logger { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task? _refreshLoop;
    private BitcoinTrackingResult? _result;
    private Currency _selectedCurrency = CurrencyModel.CurrencyListStatic[0];
    private string _query = string.Empty;
    private string? _error;
    private bool _loading;
    private bool _loadingMore;
    private bool _autoRefresh = true;
    private bool _browsingHistory;
    private bool _disposed;
    private int _operationVersion;

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

    private void SelectCurrency(string value) => _selectedCurrency =
        CurrencyModel.CurrencyListStatic.FirstOrDefault(currency => currency.Value == value) ?? _selectedCurrency;

    private Task SearchAsync() => LookupAsync(_query, clearResult: true);

    private Task RefreshAsync() => _result is null ? Task.CompletedTask : LookupAsync(_result.Query, clearResult: false);

    private async Task SelectTransactionAsync(string txId)
    {
        _query = txId;
        await SearchAsync();
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
        }

        try
        {
            var result = await TrackingService.LookupAsync(query, token);
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
            var page = await TrackingService.GetAddressTransactionsAsync(current.Query, current.NextCursor, _lifetime.Token);
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
        catch (BitcoinTrackingException ex) { _error = Localizer[$"Tracker.Error.{ex.Error}"].Value; }
        catch (Exception ex)
        {
            Logger.LogWarning("Bitcoin tracker history failed ({ExceptionType}).", ex.GetType().Name);
            _error = Localizer["Tracker.Error.Unavailable"].Value;
        }
        finally { if (!_disposed) _loadingMore = false; }
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
        _lifetime.Dispose();
    }
}
