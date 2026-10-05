using System.Collections.Concurrent;
using FreedomBlaze.Clients.BitcoinTracking;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Shared bounded daily quotes. USD candles are reused across currencies; conversion always uses
/// historical FX. Caller cancellation stops waiting while other readers retain the shared fetch.
/// </summary>
public sealed class BitcoinHistoricalPriceService : IBitcoinHistoricalPriceService, IDisposable
{
    private static readonly DateOnly BitcoinGenesis = new(2009, 1, 3);
    private readonly BitcoinHistoricalPriceClient _client;
    private readonly BitcoinHistoricalPriceOptions _options;
    private readonly TimeProvider _time;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _inFlight = new(StringComparer.Ordinal);

    public BitcoinHistoricalPriceService(BitcoinHistoricalPriceClient client,
        IOptions<BitcoinHistoricalPriceOptions> options, TimeProvider time)
    {
        _client = client;
        _options = options.Value;
        _time = time;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxCacheEntries });
    }

    public async Task<BitcoinHistoricalPriceResult> GetDailyPriceAsync(DateOnly utcDate, string currency,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = (currency ?? string.Empty).Trim().ToUpperInvariant();
        var result = new BitcoinHistoricalPriceResult { Date = utcDate, Currency = normalized };
        if (!CurrencyModel.CurrencyListStatic.Any(value => value.Value == normalized))
            return result with { Status = BitcoinHistoricalPriceStatus.UnsupportedCurrency };
        if (utcDate >= DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime))
            return result with { Status = BitcoinHistoricalPriceStatus.DayNotComplete };
        if (utcDate < BitcoinGenesis)
            return result with { Status = BitcoinHistoricalPriceStatus.NoData };

        try
        {
            var dayKey = utcDate.DayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var usdTask = GetSharedAsync($"usd:{dayKey}",
                token => _client.GetUsdCloseAsync(utcDate, token), cancellationToken);
            // USD needs no FX request. For other currencies, both source quotes can fetch in
            // parallel through the client's two-request limit and are independently cached.
            var fxTask = normalized == "USD" ? null
                : GetSharedAsync($"fx:{dayKey}:{normalized}",
                    token => _client.GetHistoricalFxAsync(utcDate, normalized, token), cancellationToken);
            if (fxTask is not null)
                await Task.WhenAll(usdTask, fxTask);
            var usd = await usdTask;
            var fx = fxTask is null ? null : await fxTask;
            var factor = fx?.Rate ?? 1m;
            var price = checked(usd.Price * factor);
            if (price <= 0)
                return result with { Status = BitcoinHistoricalPriceStatus.Unavailable };
            return result with
            {
                Status = BitcoinHistoricalPriceStatus.Available,
                PricePerBitcoin = price, BitcoinUsdClose = usd.Price, UsdToCurrencyRate = factor,
                FxDate = fx?.Date,
                RetrievedAt = fx is null || usd.RetrievedAt >= fx.RetrievedAt ? usd.RetrievedAt : fx.RetrievedAt,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BitcoinHistoricalPriceProviderException exception)
        {
            return result with { Status = exception.Status };
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException
            or InvalidOperationException or OperationCanceledException)
        {
            // Invalid provider shapes, decimal/date arithmetic and producer timeout are all a
            // missing estimate. No provider exception text or body reaches the UI.
            return result with { Status = BitcoinHistoricalPriceStatus.Unavailable };
        }
    }

    private async Task<T> GetSharedAsync<T>(string key, Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheEntry entry;
        if (TryReadCache(key, out var cached))
            entry = cached!;
        else
        {
            if (_inFlight.Count >= _options.MaxCacheEntries && !_inFlight.ContainsKey(key))
                throw new BitcoinHistoricalPriceProviderException(BitcoinHistoricalPriceStatus.RateLimited);
            var shared = _inFlight.GetOrAdd(key, _ => new Lazy<Task<CacheEntry>>(
                () => FetchAndCacheAsync(key, fetch), LazyThreadSafetyMode.ExecutionAndPublication));
            entry = await shared.Value.WaitAsync(cancellationToken);
        }
        if (entry.Status is { } status)
            throw new BitcoinHistoricalPriceProviderException(status);
        return (T)entry.Value!;
    }

    private bool TryReadCache(string key, out CacheEntry? entry)
    {
        if (_cache.TryGetValue(key, out entry) && entry is not null && _time.GetUtcNow() < entry.ExpiresAt)
            return true;
        entry = null;
        return false;
    }

    private async Task<CacheEntry> FetchAndCacheAsync<T>(string key, Func<CancellationToken, Task<T>> fetch)
        where T : class
    {
        try
        {
            if (TryReadCache(key, out var cached))
                return cached!;
            using var timeout = new CancellationTokenSource(_options.RequestTimeout, _time);
            CacheEntry entry;
            try
            {
                entry = new(await fetch(timeout.Token), null, _time.GetUtcNow() + _options.CacheDuration);
            }
            catch (BitcoinHistoricalPriceProviderException exception)
            {
                entry = new(null, exception.Status, _time.GetUtcNow() + _options.FailureCooldown);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ArgumentOutOfRangeException
                or OverflowException or InvalidOperationException)
            {
                entry = new(null, BitcoinHistoricalPriceStatus.Unavailable, _time.GetUtcNow() + _options.FailureCooldown);
            }
            _cache.Set(key, entry, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = entry.Status is null ? _options.CacheDuration : _options.FailureCooldown,
            });
            return entry;
        }
        finally { _inFlight.TryRemove(key, out _); }
    }

    public void Dispose() => _cache.Dispose();

    private sealed record CacheEntry(object? Value, BitcoinHistoricalPriceStatus? Status, DateTimeOffset ExpiresAt);
}
