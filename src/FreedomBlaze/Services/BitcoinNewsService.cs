using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Clients;
using FreedomBlaze.Constants;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Orchestrates Bitcoin news delivery and is the server-side implementation of
/// <see cref="IBitcoinNewsApi"/>: when the news component renders on the server it resolves this
/// directly (no loopback HTTP), and in WebAssembly it resolves <c>BitcoinNewsApiClient</c> instead.
/// <para>
/// For a given day it serves the in-memory cache or the database (<see cref="INewsStore"/>) when
/// available, and otherwise triggers a live web-search generation via <see cref="OpenAiNewsClient"/>.
/// Every generated set — whether produced on first request or by an explicit refresh — is persisted
/// so it survives restarts and populates the date filter. Thumbnails are resolved best-effort by
/// <see cref="IArticleThumbnailResolver"/>.
/// </para>
/// </summary>
public class BitcoinNewsService(
    OpenAiNewsClient newsClient,
    INewsStore newsStore,
    IArticleThumbnailResolver thumbnailResolver,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IOptions<OpenAiOptions> options,
    ILogger<BitcoinNewsService> logger) : IBitcoinNewsApi
{
    private readonly OpenAiOptions _options = options.Value;

    // Single-flight guard shared across (scoped) instances: only one generation runs at a time,
    // so concurrent requests await the same web-search call instead of each starting their own.
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);

    // Upper bound for a single generation, independent of any inbound request's lifetime.
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(4);

    /// <summary>The current date in the app's local time zone.</summary>
    public DateOnly Today => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    /// <summary>The dates that already have a saved news set (most recent first).</summary>
    public async Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
        => [.. await newsStore.GetAvailableDatesAsync(cancellationToken)];

    /// <summary>
    /// Returns the Bitcoin news set for <paramref name="date"/>. It is served from the in-memory
    /// cache or the database whenever available; a (paid) web-search call is made only when neither
    /// already has that day's set.
    /// </summary>
    public async Task<List<NewsArticleModel>> GetNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);
        var cacheKey = CacheKeys.BitcoinNews(date);

        // Fast path: cache or database, no lock.
        var existing = await TryGetExistingAsync(date, cacheKey, cancellationToken);
        if (existing is not null)
        {
            return [.. existing];
        }

        // Wait on the gate without the request token: a waiting request whose client has
        // disconnected must not throw here — it should still receive the set the in-flight
        // generation produces (or produce it itself). The wait is bounded by the generation
        // timeout, so it can never block indefinitely.
        await GenerationGate.WaitAsync(CancellationToken.None);
        try
        {
            // Re-check both layers under the gate: another caller may have produced and persisted
            // this day while we waited, so we never make a redundant (paid) call for a saved day.
            existing = await TryGetExistingAsync(date, cacheKey, CancellationToken.None);
            if (existing is not null)
            {
                return [.. existing];
            }

            return [.. await GenerateAndStoreAsync(date, cacheKey)];
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    /// <summary>
    /// Forces a fresh web-search generation for <paramref name="date"/>, overwriting the cached and
    /// persisted set. Triggers a paid OpenAI call, so user-facing refreshes should be rate-limited.
    /// </summary>
    public async Task<List<NewsArticleModel>> RefreshNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);

        await GenerationGate.WaitAsync(CancellationToken.None);
        try
        {
            return [.. await GenerateAndStoreAsync(date, CacheKeys.BitcoinNews(date))];
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    private DateOnly ClampToToday(DateOnly date)
    {
        var today = Today;
        return date > today ? today : date;
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

    /// <summary>
    /// Generates a fresh set via the OpenAI web-search client, resolves thumbnails, then caches and
    /// persists it. The result is cached <i>before</i> persisting so an expensive generation is never
    /// lost if the database write fails. An empty result is neither cached nor saved, so the next
    /// request retries rather than serving (and storing) nothing.
    /// </summary>
    private async Task<IReadOnlyList<NewsArticleModel>> GenerateAndStoreAsync(DateOnly date, string cacheKey)
    {
        // Deliberately decoupled from the inbound request's CancellationToken: this call is
        // expensive (~80s, paid) and its result is cached, so a client disconnect (navigation,
        // refresh, InteractiveAuto transition) must not cancel and waste it. It runs to
        // completion under its own timeout and warms the cache for the next request.
        using var cts = new CancellationTokenSource(GenerationTimeout);
        var generationToken = cts.Token;

        var articles = await newsClient.GetBitcoinNewsAsync(date, generationToken);

        if (articles.Count == 0)
        {
            logger.LogWarning("News generation for {Date} produced no articles; nothing cached or persisted.", date);
            return articles;
        }

        await thumbnailResolver.ResolveAsync(articles, generationToken);

        cache.Set(cacheKey, articles, _options.CacheDuration);
        await newsStore.SaveAsync(date, articles, _options.Model, generationToken);

        return articles;
    }
}
