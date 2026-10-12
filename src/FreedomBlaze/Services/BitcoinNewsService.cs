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
/// available. Background startup and scheduled refreshes generate missing or fresh editions via
/// <see cref="OpenAiNewsClient"/>. Every generated set is persisted
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
    ILogger<BitcoinNewsService> logger,
    BitcoinNewsRetryQueue? retryQueue = null,
    INewsGenerationStore? generationStore = null) : IBitcoinNewsApiService
{
    private readonly OpenAiOptions _options = options.Value;

    // Single-flight guard per day, shared across (scoped) instances: concurrent requests for the
    // same day await one generation instead of each starting their own, while a request for a
    // different day is never blocked behind it. One small entry per calendar day requested.
    private static readonly ConcurrentDictionary<DateOnly, SemaphoreSlim> GenerationGates = new();

    /// <summary>The current edition date in fixed GMT-3.</summary>
    public DateOnly Today => BitcoinNewsSchedule.Today(timeProvider.GetUtcNow());

    /// <summary>The dates that already have a saved news set (most recent first).</summary>
    public async Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
    {
        var dates = (await newsStore.GetAvailableDatesAsync(cancellationToken)).ToHashSet();
        if (cache.TryGetValue(CacheKeys.BitcoinNews(Today), out List<NewsArticleModel>? cached) && cached is { Count: > 0 })
            dates.Add(Today);
        return dates.OrderDescending().ToList();
    }

    /// <summary>
    /// Returns the Bitcoin news set for <paramref name="date"/>. It is served from the in-memory
    /// cache or the database. This reader never starts a paid generation. The legacy
    /// <paramref name="allowGeneration"/> argument is retained for source compatibility and ignored.
    /// </summary>
    public async Task<NewsResult> GetNewsAsync(DateOnly date, bool allowGeneration = false, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);
        var cacheKey = CacheKeys.BitcoinNews(date);

        // Fast path: cache or database, no lock.
        var existing = await TryGetExistingAsync(date, cacheKey, cancellationToken);
        if (existing is not null)
        {
            return NewsResult.Ok(existing);
        }

        // Readers can see the background worker's recent failure without starting another search.
        if (TryGetRecentFailure(date) is { } cooling)
        {
            return NewsResult.Unavailable(cooling);
        }

        if (generationStore is not null && await generationStore.GetStateAsync(date, cancellationToken) is { LastFailure: { } failure })
            return NewsResult.Unavailable(failure);

        return NewsResult.NoNews;
    }

    /// <summary>
    /// Loads an existing edition or generates it once when missing. Explicit server callers can
    /// use this to avoid replacing a saved edition; public readers use <see cref="GetNewsAsync"/>.
    /// </summary>
    public async Task<NewsResult> EnsureNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);
        var existing = await TryGetExistingAsync(date, CacheKeys.BitcoinNews(date), cancellationToken);
        if (existing is not null && !cache.TryGetValue(CacheKeys.BitcoinNewsPendingPersistence(date), out _))
            return NewsResult.Ok(existing);
        if (!IsGenerationAllowed(date)) return NewsResult.NoNews;
        return await RunGenerationAsync(date, minimumSavedTime: null, force: false, cancellationToken);
    }

    /// <summary>Runs the morning refresh at most once across replicas and restarts.</summary>
    public Task<NewsResult> RefreshScheduledNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);
        var morning = new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(-3)).ToUniversalTime();
        return RunGenerationAsync(date, morning, force: false, cancellationToken);
    }

    /// <summary>Requests background generation for a missing current edition after an explicit retry.</summary>
    public async Task<NewsResult> RetryNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var operation = await StartRetryAsync(date, cancellationToken);
        return operation.IsCompleted ? operation.Result! : await retryQueue!.RequestAsync(cancellationToken);
    }

    public async Task<NewsRetryOperation> StartRetryAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        date = ClampToToday(date);
        var existing = await TryGetExistingAsync(date, CacheKeys.BitcoinNews(date), cancellationToken);
        if (existing is not null)
            return new(Guid.Empty, true, cache.TryGetValue(CacheKeys.BitcoinNewsPendingPersistence(date), out _)
                ? await EnsureNewsAsync(date, cancellationToken) : NewsResult.Ok(existing));
        if (date != Today)
            return new(Guid.Empty, true, await GetNewsAsync(date, cancellationToken: cancellationToken));
        if (TryGetRecentFailure(date) is { } cooling)
            return new(Guid.Empty, true, NewsResult.Unavailable(cooling));
        if (generationStore is not null && GenerationBlock(await generationStore.GetStateAsync(date, cancellationToken)) is { } blocked)
            return new(Guid.Empty, true, NewsResult.Unavailable(blocked));
        return retryQueue is null
            ? new(Guid.Empty, true, NewsResult.Unavailable("The background news service is unavailable. Please try again later."))
            : retryQueue.Start();
    }

    /// <summary>Shared status fallback when polling reaches another web replica.</summary>
    public async Task<NewsRetryOperation?> GetRetryStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var requestedAt = RetryRequestedAt(id);
        if (requestedAt is null) return null;
        var date = requestedAt is { } instant ? BitcoinNewsSchedule.Today(instant) : Today;
        var result = await GetNewsAsync(date, cancellationToken: cancellationToken);
        if (result.HasArticles) return new(id, true, result);
        if (generationStore is null) return null;
        var state = await generationStore.GetStateAsync(date, cancellationToken);
        if ((state.LastCompletedUtc ?? state.LastAttemptUtc) is { } completed && completed >= requestedAt
            && !state.IsRunning && state.LastFailure is { } failure)
            return new(id, true, NewsResult.Unavailable(failure));
        if (state.IsRunning && state.LastAttemptUtc is { } began
            && timeProvider.GetUtcNow() - began > _options.GenerationTimeout + TimeSpan.FromMinutes(2))
            return new(id, true, NewsResult.Unavailable("The news search was interrupted. Please retry in a moment."));
        return new(id, false, null);
    }

    private static DateTimeOffset? RetryRequestedAt(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        if (bytes[6] >> 4 != 7) return null;
        long milliseconds = 0;
        for (var index = 0; index < 6; index++) milliseconds = (milliseconds << 8) | bytes[index];
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Forces a fresh web-search generation for <paramref name="date"/>, overwriting the cached and
    /// persisted set and clearing any failure cooldown. Triggers a paid OpenAI call, so this is
    /// reserved for the scheduled worker and master-key admin endpoint.
    /// </summary>
    public async Task<NewsResult> RefreshNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
        => await RunGenerationAsync(ClampToToday(date), minimumSavedTime: null, force: true, cancellationToken);

    private async Task<NewsResult> RunGenerationAsync(DateOnly date, DateTimeOffset? minimumSavedTime, bool force,
        CancellationToken cancellationToken)
    {
        var gate = GenerationGates.GetOrAdd(date, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Acquire a SQL session lease before reading shared state or spending any API credit.
            // A database/lease failure stops the job; paying for an uncoordinated result is unsafe.
            await using var lease = generationStore is null ? null
                : await generationStore.TryAcquireLeaseAsync(date, cancellationToken);
            if (generationStore is not null && lease is null)
                return NewsResult.Unavailable("Today's news is already being prepared. Please check again shortly.");

            var key = CacheKeys.BitcoinNews(date);
            var existing = await TryGetExistingAsync(date, key, cancellationToken);
            if (existing is not null && cache.TryGetValue(CacheKeys.BitcoinNewsPendingPersistence(date), out bool needsPersistence) && needsPersistence)
            {
                await PersistAsync(date, existing, cancellationToken);
                return NewsResult.Ok(existing);
            }
            if (!force && existing is not null)
            {
                if (minimumSavedTime is null) return NewsResult.Ok(existing);
                var savedAt = await newsStore.GetUpdatedAtUtcAsync(date, cancellationToken);
                if (savedAt >= minimumSavedTime)
                {
                    // Read shared storage again, since this process may still cache the pre-08:00 edition.
                    var saved = await newsStore.LoadAsync(date, cancellationToken);
                    if (saved is { Count: > 0 }) cache.Set(key, saved, _options.CacheDuration);
                    return NewsResult.Ok(saved ?? existing.ToList());
                }
                logger.LogInformation("Bitcoin news for {Date} needs its morning refresh: saved at {SavedAt}, required at {RequiredAt}.",
                    date, savedAt, minimumSavedTime);
            }
            if (!force && TryGetRecentFailure(date) is { } cooling) return NewsResult.Unavailable(cooling);
            var state = generationStore is null ? new NewsGenerationState(0, null, false, null)
                : await generationStore.GetStateAsync(date, cancellationToken);
            if (!force && GenerationBlock(state) is { } blocked) return NewsResult.Unavailable(blocked);
            if (force)
            {
                cache.Remove(CacheKeys.BitcoinNewsFailure(date));
                cache.Remove(CacheKeys.BitcoinNewsProviderFailure);
            }
            if (generationStore is not null) await generationStore.RecordAttemptAsync(date, cancellationToken);
            logger.LogInformation("Starting Bitcoin news job for {Date}; automatic budget {Attempt}/{Maximum}, model {Model}.",
                date, state.AttemptCount + 1, _options.MaxDailyGenerationAttempts, _options.Model);
            return await GenerateAndStoreAsync(date, key, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bitcoin news storage or shared coordination failed for {Date}; no uncoordinated API retry will be made.", date);
            return NewsResult.Unavailable("Bitcoin news could not access shared storage. Please try again later.");
        }
        finally
        {
            gate.Release();
        }
    }

    private string? GenerationBlock(NewsGenerationState state)
    {
        if (state.AttemptCount >= _options.MaxDailyGenerationAttempts)
            return "Today's news search limit has been reached. Please check back after the next daily refresh.";
        var cooldownStarted = state.LastFailure is not null
            ? state.LastCompletedUtc ?? state.LastAttemptUtc : state.LastAttemptUtc;
        if ((state.LastFailure is not null || state.IsRunning) && cooldownStarted is { } attempt
            && timeProvider.GetUtcNow() - attempt < _options.FailureCooldown)
            return state.LastFailure ?? "A news search was recently attempted. Please wait before retrying.";
        return null;
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
            cache.Set(cacheKey, stored, PersistedCacheDuration(date));
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
    private async Task<NewsResult> GenerateAndStoreAsync(DateOnly date, string cacheKey, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.GenerationTimeout);
        var generationToken = cts.Token;

        List<NewsArticleModel> articles;
        try
        {
            articles = await newsClient.GetBitcoinNewsAsync(date, generationToken);
        }
        catch (NewsUnavailableException ex)
        {
            return await RecordFailureAsync(date, ex.Reason, ex.UserMessage, ex, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown is not an upstream failure and must not create a failure cooldown.
            throw;
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            return await RecordFailureAsync(
                date,
                NewsFailureReason.Timeout,
                "The news search took too long. Please try again shortly.",
                ex, cancellationToken);
        }
        catch (Exception ex)
        {
            return await RecordFailureAsync(
                date,
                NewsFailureReason.Upstream,
                "Bitcoin news is temporarily unavailable. Please try again shortly.",
                ex, cancellationToken);
        }

        try
        {
            await thumbnailResolver.ResolveAsync(articles, generationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve thumbnails for Bitcoin news on {Date}.", date);
        }

        cancellationToken.ThrowIfCancellationRequested();
        cache.Set(cacheKey, articles, _options.CacheDuration);
        cache.Remove(CacheKeys.BitcoinNewsFailure(date));
        cache.Remove(CacheKeys.BitcoinNewsProviderFailure);

        await PersistAsync(date, articles, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return NewsResult.Ok(articles);
    }

    private async Task PersistAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken)
    {
        try
        {
            // Persistence has its own database timeout; an expired web-search budget cannot erase
            // a paid, valid edition. Cached news is retained until a later persistence-only retry.
            await newsStore.SaveAsync(date, articles, _options.Model, cancellationToken);
            cache.Remove(CacheKeys.BitcoinNewsPendingPersistence(date));
            cache.Set(CacheKeys.BitcoinNews(date), articles as List<NewsArticleModel> ?? articles.ToList(), PersistedCacheDuration(date));
            await RecordOutcomeAsync(date, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cache.Set(CacheKeys.BitcoinNewsPendingPersistence(date), true, _options.CacheDuration);
            logger.LogWarning(ex, "Bitcoin news for {Date} remains cached but could not be persisted.", date);
            await RecordOutcomeAsync(date, "News was generated but could not be saved. Please retry to save it.", cancellationToken);
        }
    }

    private async Task<NewsResult> RecordFailureAsync(DateOnly date, NewsFailureReason reason, string userMessage,
        Exception exception, CancellationToken cancellationToken)
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
        await RecordOutcomeAsync(date, userMessage, cancellationToken);
        return NewsResult.Unavailable(userMessage);
    }

    private async Task RecordOutcomeAsync(DateOnly date, string? failure, CancellationToken cancellationToken)
    {
        if (generationStore is null) return;
        try { await generationStore.RecordOutcomeAsync(date, failure, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the Bitcoin news job outcome for {Date}; its paid attempt was already recorded.", date);
        }
    }

    // Other replicas can replace today's edition at 08:00. Brief caching refreshes their readers
    // through SQL; historical editions and results awaiting persistence keep the configured TTL.
    private TimeSpan PersistedCacheDuration(DateOnly date) => date == Today
        && _options.CacheDuration > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : _options.CacheDuration;
}
