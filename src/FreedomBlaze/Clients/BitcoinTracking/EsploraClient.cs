using System.Globalization;
using System.Net;
using System.Text.Json;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace FreedomBlaze.Clients.BitcoinTracking;

/// <summary>
/// Read-only Esplora access. The singleton bounds all provider traffic, including calls made from
/// InteractiveServer components that do not pass through HTTP endpoint rate limiting.
/// </summary>
public sealed class EsploraClient : IDisposable
{
    public const string HttpClientName = "BitcoinTracking";
    public const int ConfirmedPageSize = 25;
    private const long MaximumPayloadBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly BitcoinTrackingOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _requests;
    private readonly object _budgetLock = new();
    private DateTimeOffset _windowStarted;
    private int _requestCount;
    private DateTimeOffset _cooldownUntil;
    private BitcoinTrackingError _cooldownError;

    public EsploraClient(HttpClient http, IOptions<BitcoinTrackingOptions> options, TimeProvider time)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _requests = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
        _windowStarted = _time.GetUtcNow();
    }

    public Task<EsploraTransaction> GetTransactionAsync(string txId, CancellationToken cancellationToken) =>
        GetJsonAsync<EsploraTransaction>($"tx/{Uri.EscapeDataString(txId)}", cancellationToken);

    public Task<EsploraAddress> GetAddressAsync(string address, CancellationToken cancellationToken) =>
        GetJsonAsync<EsploraAddress>($"address/{Uri.EscapeDataString(address)}", cancellationToken);

    public Task<EsploraTransaction[]> GetAddressTransactionsAsync(string address, string? lastSeenTxId,
        CancellationToken cancellationToken) =>
        GetJsonAsync<EsploraTransaction[]>(lastSeenTxId is null
            ? $"address/{Uri.EscapeDataString(address)}/txs/chain"
            : $"address/{Uri.EscapeDataString(address)}/txs/chain/{Uri.EscapeDataString(lastSeenTxId)}",
            cancellationToken);

    public Task<EsploraTransaction[]> GetAddressMempoolTransactionsAsync(string address, CancellationToken cancellationToken) =>
        GetJsonAsync<EsploraTransaction[]>($"address/{Uri.EscapeDataString(address)}/txs/mempool", cancellationToken);

    public async Task<int> GetTipHeightAsync(CancellationToken cancellationToken)
    {
        var value = await GetTextAsync("blocks/tip/height", cancellationToken, allowNotFound: false);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height < 0)
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        return height;
    }

    public async Task<string> GetBestChainHashAsync(int height, CancellationToken cancellationToken)
    {
        var hash = await GetTextAsync($"block-height/{height.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken, allowNotFound: false);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        return hash.ToLowerInvariant();
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var body = await GetTextAsync(path, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(body)
                ?? throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        }
        catch (JsonException)
        {
            CoolDown(BitcoinTrackingError.Unavailable, _options.FailureCooldown);
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        }
    }

    private async Task<string> GetTextAsync(string path, CancellationToken cancellationToken, bool allowNotFound = true)
    {
        // Fail promptly instead of accumulating an unbounded queue of distinct public lookups.
        if (!await _requests.WaitAsync(0, cancellationToken))
            throw new BitcoinTrackingException(BitcoinTrackingError.RateLimited);
        try
        {
            ReserveRequest();
            // Global HttpClient tracing would otherwise export the lookup identifier in url.full.
            using var instrumentation = SuppressInstrumentationScope.Begin();
            using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - _time.GetUtcNow())
                    ?? TimeSpan.FromSeconds(30);
                CoolDown(BitcoinTrackingError.RateLimited, delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1));
                throw new BitcoinTrackingException(BitcoinTrackingError.RateLimited);
            }
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                throw new BitcoinTrackingException(BitcoinTrackingError.NotFound);
            if (!response.IsSuccessStatusCode)
            {
                CoolDown(BitcoinTrackingError.Unavailable, _options.FailureCooldown);
                throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
            }
            await response.Content.LoadIntoBufferAsync(MaximumPayloadBytes, cancellationToken);
            return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            CoolDown(BitcoinTrackingError.Unavailable, _options.FailureCooldown);
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        }
        finally
        {
            _requests.Release();
        }
    }

    private void ReserveRequest()
    {
        lock (_budgetLock)
        {
            var now = _time.GetUtcNow();
            if (now < _cooldownUntil)
                throw new BitcoinTrackingException(_cooldownError);
            if (now - _windowStarted >= TimeSpan.FromMinutes(1))
            {
                _windowStarted = now;
                _requestCount = 0;
            }
            if (_requestCount >= _options.MaxProviderRequestsPerMinute)
                throw new BitcoinTrackingException(BitcoinTrackingError.RateLimited);
            _requestCount++;
        }
    }

    private void CoolDown(BitcoinTrackingError error, TimeSpan delay)
    {
        lock (_budgetLock)
        {
            var until = _time.GetUtcNow() + delay;
            if (until > _cooldownUntil)
            {
                _cooldownUntil = until;
                _cooldownError = error;
            }
        }
    }

    public void Dispose()
    {
        _requests.Dispose();
        _http.Dispose();
    }
}
