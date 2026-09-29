using System.Collections.Concurrent;
using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Client.Models;
using FreedomBlaze.Clients;
using FreedomBlaze.Constants;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Orchestrates Bitcoin news delivery and is the server-side implementation of
/// <see cref="IBitcoinNewsApiService"/>: when the news component renders on the server it resolves this
/// directly (no loopback HTTP), and in WebAssembly it resolves <c>BitcoinNewsApiService</c> instead.
/// <para>
/// For a given day it serves the in-memory cache or the database (<see cref="INewsStore"/>) when
/// available, and otherwise triggers a live web-search generation via <see cref="OpenAiNewsClient"/>.
/// Every generated set - whether produced on first request or by an explicit refresh - is persisted
/// so it survives restarts and populates the date filter. Thumbnails are resolved best-effort by
/// <see cref="IArticleThumbnailHelper"/>.
/// </para>
/// <para>
/// Generation is deliberately defensive, because each attempt is slow and billed: at most one runs
/// per day at a time, a failure puts that day on a short cooldown, and days outside the configured
/// history window are served from storage only.
/// </para>
/// </summary>
public class BitcoinNewsService(
    OpenAiNewsClient newsClient,
    INewsStore newsStore,
    IArticleThumbnailHelper thumbnailResolver,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IOptions<OpenAiOptions> options,
    ILogger<BitcoinNewsService> logger) : IBitcoinNewsApiService
{
    private readonly OpenAiOptions _options = options.Value;

    // Single-flight guard per day, shared across (scoped) instances: concurrent requests for the
    // same day await one generation instead of each starting their own, while a request for a
    // different day is never blocked behind it. One small entry per calendar day requested.
    private static readonly ConcurrentDictionary<DateOnly, SemaphoreSlim> GenerationGates = new();

    /// <summary>The current date in the app's local time zone.</summary>
    public DateOnly Today => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    /// <summary>The dates that already have a saved news set (most recent first).</summary>
    public async Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
        => [.. await newsStore.GetAvailableDatesAsync(cancellationToken)];

    /// <summary>
    /// Returns the Bitcoin news set for <paramref name="date"/>. It is served from the in-memory
    /// cache or the database whenever available; a (paid) web-search call is made only when neither
    /// already has that day's set and <paramref name="allowGeneration"/> permits it.
    /// </summary>
    public async Task<NewsResult> GetNewsAsync(DateOnly date, bool allowGeneration = true, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);
        var cacheKey = CacheKeys.BitcoinNews(date);

        // Fast path: cache or database, no lock.
        var existing = await TryGetExistingAsync(date, cacheKey, cancellationToken);
        if (existing is not null)
        {
            return NewsResult.Ok(existing);
        }

        // A recent failure is reported even when generation is off (prerender), so the page states
        // the reason from the first paint instead of showing "no news" and correcting itself a
        // moment later. It also short-circuits before the gate: there is no point queueing behind a
        // generation for a day already known to be failing.
        if (TryGetRecentFailure(date) is { } cooling)
        {
            return NewsResult.Unavailable(cooling);
        }

        if (!allowGeneration || !IsGenerationAllowed(date))
        {
            return NewsResult.NoNews;
        }

        var gate = GenerationGates.GetOrAdd(date, _ => new SemaphoreSlim(1, 1));

        // Navigation can cancel a queued request. Once generation starts it uses its own timeout,
        // so disconnecting does not discard a paid search that is already running.
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check every layer under the gate: another caller may have produced and persisted
            // this day (or discovered it is failing) while we waited, so we never make a redundant
            // paid call.
            existing = await TryGetExistingAsync(date, cacheKey, CancellationToken.None);
            if (existing is not null)
            {
                return NewsResult.Ok(existing);
            }

            if (TryGetRecentFailure(date) is { } justFailed)
            {
                return NewsResult.Unavailable(justFailed);
            }

            return await GenerateAndStoreAsync(date, cacheKey);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Forces a fresh web-search generation for <paramref name="date"/>, overwriting the cached and
    /// persisted set and clearing any failure cooldown. Triggers a paid OpenAI call, so this is
    /// reserved for the master-key admin endpoint.
    /// </summary>
    public async Task<NewsResult> RefreshNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);

        var gate = GenerationGates.GetOrAdd(date, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            // An operator can retry immediately after fixing credentials or adding credit.
            cache.Remove(CacheKeys.BitcoinNewsFailure(date));
            cache.Remove(CacheKeys.BitcoinNewsProviderFailure);
            return await GenerateAndStoreAsync(date, CacheKeys.BitcoinNews(date));
        }
        finally
        {
            gate.Release();
        }
    }

    private DateOnly ClampToToday(DateOnly date)
    {
        var today = Today;
        return date > today ? today : date;
    }

    /// <summary>
    /// Whether a day is recent enough to be generated on demand. Older days remain readable from
    /// storage; they are simply never (re)generated, so walking the endpoint back through history
    /// cannot run up an OpenAI bill.
    /// </summary>
    private bool IsGenerationAllowed(DateOnly date)
    {
        var oldest = Today.AddDays(-Math.Max(0, _options.MaxGenerationHistoryDays));
        if (date >= oldest)
        {
            return true;
        }

        logger.LogDebug(
            "Skipping news generation for {Date}: older than the {Days}-day generation window.",
            date, _options.MaxGenerationHistoryDays);
        return false;
    }

    /// <summary>
    /// Returns the cached set, or the persisted set (warming the cache), or <c>null</c> when neither
    /// has news for the day. Shared by the lock-free fast path and the post-gate re-check.
    /// </summary>
    private async Task<IReadOnlyList<NewsArticleModel>?> TryGetExistingAsync(DateOnly date, string cacheKey, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out List<NewsArticleModel>? cached) && cached is { Count: > 0 })
        {
            return cached;
        }

        var stored = await newsStore.LoadAsync(date, cancellationToken);
        if (stored is { Count: > 0 })
        {
            cache.Set(cacheKey, stored, _options.CacheDuration);
            return stored;
        }

        return null;
    }

    /// <summary>The reader-facing message from a recent failed generation, or <c>null</c> if the day is clear.</summary>
    private string? TryGetRecentFailure(DateOnly date)
    {
        if (cache.TryGetValue(CacheKeys.BitcoinNewsProviderFailure, out string? providerMessage))
        {
            return providerMessage;
        }

        return cache.TryGetValue(CacheKeys.BitcoinNewsFailure(date), out string? message) ? message : null;
    }

    /// <summary>
    /// Generates a fresh set via the OpenAI web-search client, resolves thumbnails, then caches and
    /// persists it. The result is cached <i>before</i> persisting so an expensive generation is never
    /// lost if the database write fails. A failure is remembered for the configured cooldown so the
    /// next visitor gets an immediate explanation instead of another slow, paid, doomed attempt.
    /// </summary>
    private async Task<NewsResult> GenerateAndStoreAsync(DateOnly date, string cacheKey)
    {
        // Deliberately decoupled from the inbound request's CancellationToken: this call is
        // expensive (~80s, paid) and its result is cached, so a client disconnect (navigation,
        // refresh, InteractiveAuto transition) must not cancel and waste it. It runs to
        // completion under its own timeout and warms the cache for the next request.
        using var cts = new CancellationTokenSource(_options.GenerationTimeout);
        var generationToken = cts.Token;

        List<NewsArticleModel> articles;
        try
        {
            articles = await newsClient.GetBitcoinNewsAsync(date, generationToken);
        }
        catch (NewsUnavailableException ex)
        {
            return RecordFailure(date, ex.Reason, ex.UserMessage, ex);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            return RecordFailure(
                date,
                NewsFailureReason.Timeout,
                "The news search took too long. Please try again shortly.",
                ex);
        }
        catch (Exception ex)
        {
            return RecordFailure(
                date,
                NewsFailureReason.Upstream,
                "Bitcoin news is temporarily unavailable. Please try again shortly.",
                ex);
        }

        try
        {
            await thumbnailResolver.ResolveAsync(articles, generationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve thumbnails for Bitcoin news on {Date}.", date);
        }

        cache.Set(cacheKey, articles, _options.CacheDuration);
        cache.Remove(CacheKeys.BitcoinNewsFailure(date));
        cache.Remove(CacheKeys.BitcoinNewsProviderFailure);

        try
        {
            await newsStore.SaveAsync(date, articles, _options.Model, generationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bitcoin news for {Date} remains cached but could not be persisted.", date);
        }

        return NewsResult.Ok(articles);
    }

    private NewsResult RecordFailure(DateOnly date, NewsFailureReason reason, string userMessage, Exception exception)
    {
        logger.LogError(exception,
            "Bitcoin news generation for {Date} failed ({Reason}); pausing attempts for {Cooldown}.",
            date, reason, _options.FailureCooldown);

        // Changing the date cannot fix an exhausted account or a rejected configuration.
        // Keep existing articles available, but avoid repeating the same failing API request.
        var failureKey = reason is NewsFailureReason.NotConfigured or NewsFailureReason.QuotaExceeded
            or NewsFailureReason.Unauthorized or NewsFailureReason.ModelRejected
            ? CacheKeys.BitcoinNewsProviderFailure
            : CacheKeys.BitcoinNewsFailure(date);
        cache.Set(failureKey, userMessage, _options.FailureCooldown);
        return NewsResult.Unavailable(userMessage);
    }
}
