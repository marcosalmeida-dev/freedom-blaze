using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;

namespace FreedomBlaze;

/// <summary>Shared market data, loaded on demand and refreshed without overlapping requests.</summary>
public sealed class AppState(
    IExchangeRateService exchangeRateService,
    ILogger<AppState> logger,
    TimeProvider timeProvider) : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialLoad;
    private Task? _refresh;
    private Task? _refreshLoop;
    private Task? _disposal;
    private bool _disposed;

    public BitcoinExchangeRateModel? BitcoinExchangeRate { get; private set; }
    public DateTimeOffset LastUpdate { get; private set; }
    public List<BitcoinExchangeStatusModel> ExchangeStatusList { get; private set; } = [];
    public bool HasRateError { get; private set; }

    public event Func<Task>? OnChange;

    /// <summary>
    /// Starts updates when an interactive consumer needs rates. Canceling a consumer's wait
    /// does not cancel the request shared by other pages and circuits.
    /// </summary>
    public Task EnsureRatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _initialLoad ??= GetOrStartRefresh();
            _refreshLoop ??= RefreshLoopAsync(_initialLoad);
            return _initialLoad.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Retries a refresh; simultaneous callers share the same pending operation.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetOrStartRefresh().WaitAsync(cancellationToken);
        }
    }

    private Task GetOrStartRefresh()
    {
        if (_refresh is null || _refresh.IsCompleted)
            _refresh = UpdateExchangeRateAsync();

        return _refresh;
    }

    private async Task RefreshLoopAsync(Task initialLoad)
    {
        try
        {
            await initialLoad.ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, _lifetime.Token).ConfigureAwait(false);
                await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    private async Task UpdateExchangeRateAsync()
    {
        // Publish the task before a synchronous cache hit can invoke any subscribers.
        await Task.Yield();

        try
        {
            var rate = await exchangeRateService.GetExchangeRateAsync(_lifetime.Token).ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();

            HasRateError = rate is null;
            if (rate is not null && !ReferenceEquals(BitcoinExchangeRate, rate))
            {
                BitcoinExchangeRate = rate;
                LastUpdate = timeProvider.GetUtcNow();
            }

            // Keep the last usable rate on failure, but always publish current source health.
            ExchangeStatusList = exchangeRateService.BitcoinExchangeStatusList.ToList();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            HasRateError = true;
            logger.LogError(exception, "An error occurred while updating the exchange rate in AppState.");
        }

        await NotifyStateChangedAsync().ConfigureAwait(false);
    }

    private Task NotifyStateChangedAsync()
    {
        var subscribers = OnChange?.GetInvocationList();
        return subscribers is null
            ? Task.CompletedTask
            : Task.WhenAll(subscribers.Cast<Func<Task>>().Select(NotifySubscriberAsync));
    }

    private async Task NotifySubscriberAsync(Func<Task> subscriber)
    {
        try
        {
            await subscriber().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A disconnected circuit must not prevent other readers from receiving updates.
            logger.LogDebug(exception, "An exchange-rate subscriber could not receive an update.");
        }
    }

    public void Dispose() => _ = DisposeAsync();

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposal is not null)
                return new ValueTask(_disposal);

            _disposed = true;
            _disposal = DisposeCoreAsync();
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_refresh ?? Task.CompletedTask, _refreshLoop ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Exchange-rate updates stopped during shutdown.");
        }
        finally
        {
            OnChange = null;
            _lifetime.Dispose();
        }
    }
}
