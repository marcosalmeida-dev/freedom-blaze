using System.Globalization;
using FreedomBlaze.Client.Enums;
using FreedomBlaze.Client.Helpers;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MudBlazor;

namespace FreedomBlaze.Components.Pages;

public partial class SatsConverter : IAsyncDisposable
{
    private static readonly decimal[] ReferenceSatsAmounts = [1_000m, 10_000m];

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private CultureService CultureService { get; set; } = default!;
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ILogger<SatsConverter> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<Resources.Localization> Localizer { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private SatsConversionState _state = default!;
    private IJSObjectReference? _module;
    private BitcoinExchangeRateModel? _appliedRates;
    private ConverterPreferences? _savedPreferences;
    private DateTimeOffset _formattedUpdate;
    private string? _lastUpdateFormatted;
    private string? _error;
    private bool _loading = true;
    private bool _initializing = true;
    private bool _satsFieldOnTop;
    private bool _disposed;
    private bool _preferenceWarningShown;

    private CultureInfo DisplayCulture => CultureService.CurrentCulture;

    private string FormatReferenceAmount(decimal sats) => _state.HasRates
        ? $"≈ {(_state.SelectedCurrency.BitcoinPrice * (sats / CurrencyConverterHelper.SatoshiPerBitcoin)).ToString("C", _state.SelectedCurrency.CultureInfo)}"
        : "—";

    protected override void OnInitialized()
    {
        _state = new SatsConversionState("en-US");
        ApplyRates();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        AppState.OnChange += OnAppStateChangedAsync;
        try
        {
            await RestorePreferencesAsync();
            _initializing = false;
            await AppState.EnsureRatesAsync(_lifetime.Token);
            if (!_disposed)
            {
                ApplyRates();
                await FormatLastUpdatedAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) { }
        finally
        {
            _initializing = false;
            _loading = false;
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async Task OnAppStateChangedAsync()
    {
        if (_disposed)
            return;

        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed)
                    return;
                ApplyRates();
                await FormatLastUpdatedAsync();
                if (!_disposed)
                    StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) { }
    }

    private void ApplyRates()
    {
        if (!ReferenceEquals(_appliedRates, AppState.BitcoinExchangeRate))
        {
            _appliedRates = AppState.BitcoinExchangeRate;
            _state.UpdateRates(_appliedRates);
        }

        _error = AppState.HasRateError
            ? _state.HasRates
                ? Localizer["SatsConverter.StaleRates"].Value
                : Localizer["SatsConverter.NoRates"].Value
            : _appliedRates is not null && !_state.HasRates
                ? Localizer["SatsConverter.MissingCurrencyRate"].Value
                : null;
    }

    private async Task RefreshRatesAsync()
    {
        if (_loading || _disposed)
            return;

        _loading = true;
        try
        {
            await AppState.RefreshAsync(_lifetime.Token);
            if (!_disposed)
            {
                ApplyRates();
                await FormatLastUpdatedAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) { }
        finally
        {
            _loading = false;
        }
    }

    private async Task SelectCurrencyAsync(string? cultureName)
    {
        if (_initializing || _disposed || string.IsNullOrEmpty(cultureName)
            || cultureName == _state.SelectedCurrency.CultureName)
            return;

        _state.SelectCurrency(cultureName);
        ApplyRates();
        await SavePreferencesAsync();
    }

    private Task SetCurrencyAmountAsync(decimal? amount)
    {
        _state.SetCurrencyAmount(amount);
        return SavePreferencesAsync();
    }

    private Task SetSatsAmountAsync(decimal? amount)
    {
        _state.SetSatsAmount(amount);
        return SavePreferencesAsync();
    }

    private void SwapFields() => _satsFieldOnTop = !_satsFieldOnTop;

    private async Task RestorePreferencesAsync()
    {
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "./js/satsConverter.js");
            _formattedUpdate = default;
            var preferences = await _module.InvokeAsync<ConverterPreferences?>("readPreferences", _lifetime.Token);
            if (preferences is null)
                return;

            // Currency is a converter preference; the site language only controls presentation.
            _state.SelectCurrency(preferences.CultureName);
            if (!Enum.TryParse<ConversionType>(preferences.Direction, out var direction)
                || direction is not (ConversionType.BitcoinToCurrency or ConversionType.CurrencyToBitcoin))
                return;

            decimal? amount = null;
            if (!string.IsNullOrEmpty(preferences.Amount))
            {
                if (!decimal.TryParse(preferences.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 0 || parsed > SatsConversionState.MaximumAmount)
                    return;
                amount = parsed;
            }

            if (direction == ConversionType.CurrencyToBitcoin)
                _state.SetCurrencyAmount(amount);
            else
                _state.SetSatsAmount(amount);

            _savedPreferences = GetPreferences();
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Converter preferences could not be restored.");
        }
    }

    private ConverterPreferences GetPreferences() => new(
        _state.Direction.ToString(),
        (_state.Direction == ConversionType.CurrencyToBitcoin ? _state.CurrencyAmount : _state.SatsAmount)?.ToString(CultureInfo.InvariantCulture),
        _state.SelectedCurrency.CultureName);

    private async Task SavePreferencesAsync()
    {
        var preferences = GetPreferences();
        if (_module is null || _disposed || preferences == _savedPreferences)
            return;

        try
        {
            // One browser call stores the source amount, direction and currency atomically.
            await _module.InvokeVoidAsync("savePreferences", _lifetime.Token, preferences);
            _savedPreferences = preferences;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) { }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Converter preferences could not be saved.");
            if (!_preferenceWarningShown)
            {
                _preferenceWarningShown = true;
                Snackbar.Add(Localizer["SatsConverter.PreferencesUnavailable"], Severity.Info);
            }
        }
    }

    private async Task FormatLastUpdatedAsync()
    {
        if (AppState.LastUpdate == default || AppState.LastUpdate == _formattedUpdate)
            return;

        var updated = AppState.LastUpdate;
        // UTC remains a useful fallback when browser time formatting is unavailable.
        _lastUpdateFormatted = updated.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);
        if (_module is not null)
        {
            try
            {
                _lastUpdateFormatted = await _module.InvokeAsync<string>("formatTime", _lifetime.Token,
                    updated.ToUnixTimeMilliseconds(), CultureService.CurrentCulture.Name);
            }
            catch (JSException ex)
            {
                Logger.LogDebug(ex, "Local converter time could not be formatted.");
            }
        }
        _formattedUpdate = updated;
    }

    private async Task CopyAsync(decimal? value)
    {
        if (!value.HasValue || _disposed)
            return;

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", _lifetime.Token, value.Value.ToString(DisplayCulture));
            Snackbar.Add(Localizer["SatsConverter.Copied"], Severity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) { }
        catch (JSException)
        {
            Snackbar.Add(Localizer["SatsConverter.ClipboardUnavailable"], Severity.Info);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        AppState.OnChange -= OnAppStateChangedAsync;
        await _lifetime.CancelAsync();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }

    public sealed record ConverterPreferences(string Direction, string? Amount, string? CultureName);
}
