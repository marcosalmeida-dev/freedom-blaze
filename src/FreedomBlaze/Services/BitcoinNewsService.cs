using FreedomBlaze.Constants;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Orchestrates Bitcoin news delivery: serves a cached daily set when possible and otherwise
/// triggers a live web-search generation via <see cref="OpenAiNewsClient"/>. Generated sets are
/// cached in memory and persisted through the configured <see cref="INewsStore"/> (local files by
/// default, optionally Azure Blob) so they survive restarts. Article thumbnails are resolved
/// best-effort from each source's Open Graph image.
/// </summary>
public class BitcoinNewsService
{
    /// <summary>Named <see cref="HttpClient"/> used to scrape article thumbnails.</summary>
    public const string ImageScraperHttpClient = "ArticleImageScraper";

    private const string DefaultImagePath = "img/articles/default-img.png";

    private readonly OpenAiNewsClient _newsClient;
    private readonly INewsStore _newsStore;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BitcoinNewsService> _logger;
    private readonly OpenAiOptions _options;

    // Single-flight guard shared across (scoped) instances: only one generation runs at a time,
    // so concurrent requests await the same web-search call instead of each starting their own.
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);

    // Upper bound for a single generation, independent of any inbound request's lifetime.
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(4);

    public BitcoinNewsService(
        OpenAiNewsClient newsClient,
        INewsStore newsStore,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        IOptions<OpenAiOptions> options,
        ILogger<BitcoinNewsService> logger)
    {
        _newsClient = newsClient;
        _newsStore = newsStore;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns today's Bitcoin news, generating it on first request of the day.</summary>
    public async Task<IReadOnlyList<NewsArticleModel>> GetTodayBitcoinNewsAsync(CancellationToken cancellationToken = default)
    {
        var date = _timeProvider.GetLocalNow().Date;
        var cacheKey = CacheKeys.BitcoinNews(date);

        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var stored = await _newsStore.LoadAsync(date, cancellationToken);
        if (stored is { Count: > 0 })
        {
            _cache.Set(cacheKey, stored, _options.CacheDuration);
            return stored;
        }

        // Wait on the gate without the request token: a waiting request whose client has
        // disconnected must not throw here — it should still receive the set the in-flight
        // generation produces (or produce it itself). The wait is bounded by the generation
        // timeout, so it can never block indefinitely.
        await GenerationGate.WaitAsync(CancellationToken.None);
        try
        {
            // Another caller may have produced the set while we waited for the gate.
            if (TryGetCached(cacheKey, out cached))
            {
                return cached;
            }

            return await GenerateAndStoreAsync(date, cacheKey);
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    /// <summary>
    /// Forces a fresh web-search generation for today, overwriting the cached/persisted set.
    /// Note: this triggers a paid OpenAI call, so callers should rate-limit user-facing refreshes.
    /// </summary>
    public async Task<IReadOnlyList<NewsArticleModel>> RefreshBitcoinNewsAsync(CancellationToken cancellationToken = default)
    {
        var date = _timeProvider.GetLocalNow().Date;
        var cacheKey = CacheKeys.BitcoinNews(date);

        await GenerationGate.WaitAsync(CancellationToken.None);
        try
        {
            return await GenerateAndStoreAsync(date, cacheKey);
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    private async Task<List<NewsArticleModel>> GenerateAndStoreAsync(DateTime date, string cacheKey)
    {
        // Deliberately decoupled from the inbound request's CancellationToken: this call is
        // expensive (~80s, paid) and its result is cached, so a client disconnect (navigation,
        // refresh, InteractiveAuto transition) must not cancel and waste it. It runs to
        // completion under its own timeout and warms the cache for the next request.
        using var cts = new CancellationTokenSource(GenerationTimeout);
        var generationToken = cts.Token;

        var articles = await _newsClient.GetBitcoinNewsAsync(generationToken);

        await ResolveThumbnailsAsync(articles, generationToken);

        _cache.Set(cacheKey, articles, _options.CacheDuration);
        await _newsStore.SaveAsync(date, articles, generationToken);

        return articles;
    }

    private bool TryGetCached(string cacheKey, out IReadOnlyList<NewsArticleModel> cached)
    {
        if (_cache.TryGetValue(cacheKey, out List<NewsArticleModel>? value) && value is not null)
        {
            cached = value;
            return true;
        }

        cached = [];
        return false;
    }

    private async Task ResolveThumbnailsAsync(List<NewsArticleModel> articles, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ImageScraperHttpClient);

        await Task.WhenAll(articles
            .Where(a => string.IsNullOrEmpty(a.NewsThumbImg))
            .Select(async article =>
            {
                article.NewsThumbImg =
                    await TryGetOpenGraphImageAsync(client, article.ArticleLinkUrl, cancellationToken)
                    ?? DefaultImagePath;
            }));
    }

    private async Task<string?> TryGetOpenGraphImageAsync(HttpClient client, string articleUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(articleUrl) || !Uri.TryCreate(articleUrl, UriKind.Absolute, out var articleUri))
        {
            return null;
        }

        // Per-article timeout so one slow/blocking source can't stall the whole batch.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            var html = await client.GetStringAsync(articleUri, timeout.Token);

            var document = new HtmlDocument();
            document.LoadHtml(html);

            var ogImage = document.DocumentNode
                .SelectSingleNode("//meta[@property='og:image' or @name='og:image' or @property='twitter:image']")
                ?.GetAttributeValue("content", null);

            if (string.IsNullOrWhiteSpace(ogImage))
            {
                return null;
            }

            // Resolve protocol-relative or root-relative image URLs against the article URL.
            return Uri.TryCreate(articleUri, ogImage, out var absolute) ? absolute.ToString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not fetch Open Graph image for {Url}.", articleUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error while parsing article {Url}.", articleUrl);
            return null;
        }
    }
}
