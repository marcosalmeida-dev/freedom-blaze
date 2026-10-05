using System.Globalization;
using System.Net;
using System.Text.Json;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace FreedomBlaze.Clients.BitcoinTracking;

/// <summary>
/// Keyless public historical data. Limits actual HTTP attempts across both providers, allows a
/// bounded queue for one history page, and never retries or uses a latest-price endpoint.
/// </summary>
public sealed class BitcoinHistoricalPriceClient : IDisposable
{
    public const string CandleHttpClientName = "BitcoinHistoricalCandles";
    public const string FxHttpClientName = "BitcoinHistoricalFx";
    private const long MaximumPayloadBytes = 1024 * 1024;
    private readonly HttpClient _candles;
    private readonly HttpClient _fx;
    private readonly BitcoinHistoricalPriceOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _requests;
    private readonly object _budgetLock = new();
    private DateTimeOffset _windowStarted;
    private int _requestCount;
    private int _waitingCount;
    private long _nextRequestTimestamp;
    private DateTimeOffset _cooldownUntil;
    private BitcoinHistoricalPriceStatus _cooldownStatus;

    public BitcoinHistoricalPriceClient(HttpClient candles, HttpClient fx,
        IOptions<BitcoinHistoricalPriceOptions> options, TimeProvider time)
    {
        _candles = candles;
        _fx = fx;
        _options = options.Value;
        _time = time;
        _requests = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
        _windowStarted = _time.GetUtcNow();
    }

    internal async Task<DailyUsdClose> GetUsdCloseAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var path = "products/BTC-USD/candles?granularity=86400&start="
            + start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            + "&end=" + end.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var json = await GetTextAsync(_candles, path, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement;
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 300)
                throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
            var timestamp = new DateTimeOffset(start).ToUnixTimeSeconds();
            decimal? close = null;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6
                    || !row[0].TryGetInt64(out var rowTimestamp))
                    throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
                // Coinbase can return candles outside the requested start/end range, in reverse
                // order. Only the exact UTC bucket is relevant; never use an adjacent day's price.
                if (rowTimestamp != timestamp)
                    continue;
                if (close is not null || !row[4].TryGetDecimal(out var value) || value <= 0)
                    throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
                close = value;
            }
            return close is { } price
                ? new DailyUsdClose(price, _time.GetUtcNow())
                : throw Failure(BitcoinHistoricalPriceStatus.NoData);
        }
        catch (JsonException)
        {
            CoolDown(BitcoinHistoricalPriceStatus.Unavailable, _options.FailureCooldown);
            throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
        }
    }

    internal async Task<HistoricalFxQuote> GetHistoricalFxAsync(DateOnly date, string currency,
        CancellationToken cancellationToken)
    {
        var path = $"v2/rate/usd/{currency.ToLowerInvariant()}?date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var json = await GetTextAsync(_fx, path, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("base", out var baseValue) || baseValue.GetString() != "USD"
                || !root.TryGetProperty("quote", out var quoteValue) || quoteValue.GetString() != currency
                || !root.TryGetProperty("date", out var dateValue)
                || !DateOnly.TryParseExact(dateValue.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var observationDate)
                || observationDate > date || observationDate < date.AddDays(-7)
                || !root.TryGetProperty("rate", out var rateValue) || !rateValue.TryGetDecimal(out var rate) || rate <= 0)
                throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
            return new HistoricalFxQuote(rate, observationDate, _time.GetUtcNow());
        }
        catch (JsonException)
        {
            CoolDown(BitcoinHistoricalPriceStatus.Unavailable, _options.FailureCooldown);
            throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
        }
    }

    private async Task<string> GetTextAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        // A page can contain many confirmation dates. Queue those requests while bounding both
        // waiting memory and actual provider concurrency; unrelated unlimited batches are rejected.
        var waiting = Interlocked.Increment(ref _waitingCount);
        try
        {
            if (waiting > _options.MaxQueuedRequests)
                throw Failure(BitcoinHistoricalPriceStatus.RateLimited);
            await _requests.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _waitingCount);
        }

        try
        {
            await PaceRequestAsync(cancellationToken);
            ReserveRequest();
            // Dates/currencies can correlate with a user's transaction history. Do not export
            // request URLs through the globally configured HttpClient trace instrumentation.
            using var instrumentation = SuppressInstrumentationScope.Begin();
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - _time.GetUtcNow())
                    ?? TimeSpan.FromSeconds(30);
                CoolDown(BitcoinHistoricalPriceStatus.RateLimited, delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1));
                throw Failure(BitcoinHistoricalPriceStatus.RateLimited);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw Failure(BitcoinHistoricalPriceStatus.NoData);
            if (!response.IsSuccessStatusCode)
            {
                CoolDown(BitcoinHistoricalPriceStatus.Unavailable, _options.FailureCooldown);
                throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
            }
            await response.Content.LoadIntoBufferAsync(MaximumPayloadBytes, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            CoolDown(BitcoinHistoricalPriceStatus.Unavailable, _options.FailureCooldown);
            throw Failure(BitcoinHistoricalPriceStatus.Unavailable);
        }
        finally { _requests.Release(); }
    }

    private async Task PaceRequestAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_budgetLock)
        {
            // Coinbase public market data permits ten requests per second per IP. A shared
            // eight-per-second ceiling also protects against fast-response page-load bursts.
            var now = _time.GetTimestamp();
            var slot = Math.Max(now, _nextRequestTimestamp);
            delay = _time.GetElapsedTime(now, slot);
            _nextRequestTimestamp = checked(slot + _time.TimestampFrequency / 8);
        }
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, _time, cancellationToken);
    }

    private void ReserveRequest()
    {
        lock (_budgetLock)
        {
            var now = _time.GetUtcNow();
            if (now < _cooldownUntil)
                throw Failure(_cooldownStatus);
            if (now - _windowStarted >= TimeSpan.FromMinutes(1))
            {
                _windowStarted = now;
                _requestCount = 0;
            }
            if (_requestCount >= _options.MaxProviderRequestsPerMinute)
                throw Failure(BitcoinHistoricalPriceStatus.RateLimited);
            _requestCount++;
        }
    }

    private void CoolDown(BitcoinHistoricalPriceStatus status, TimeSpan delay)
    {
        lock (_budgetLock)
        {
            var until = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 3_600));
            if (until > _cooldownUntil)
            {
                _cooldownUntil = until;
                _cooldownStatus = status;
            }
        }
    }

    private static BitcoinHistoricalPriceProviderException Failure(BitcoinHistoricalPriceStatus status) => new(status);

    public void Dispose()
    {
        _requests.Dispose();
        _candles.Dispose();
        _fx.Dispose();
    }
}

internal sealed record DailyUsdClose(decimal Price, DateTimeOffset RetrievedAt);
internal sealed record HistoricalFxQuote(decimal Rate, DateOnly Date, DateTimeOffset RetrievedAt);
internal sealed class BitcoinHistoricalPriceProviderException(BitcoinHistoricalPriceStatus status) : Exception(status.ToString())
{
    public BitcoinHistoricalPriceStatus Status { get; } = status;
}
