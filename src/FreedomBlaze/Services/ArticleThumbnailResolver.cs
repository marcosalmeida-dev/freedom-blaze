using FreedomBlaze.Models;
using HtmlAgilityPack;

namespace FreedomBlaze.Services;

/// <summary>
/// Resolves a display thumbnail for each news article, best-effort, by scraping the article page's
/// Open Graph (<c>og:image</c>) metadata. Articles that already carry a thumbnail are left
/// untouched; any that can't be resolved fall back to a bundled default image.
/// </summary>
public interface IArticleThumbnailResolver
{
    /// <summary>
    /// Fills in <see cref="NewsArticleModel.NewsThumbImg"/> for every article that lacks one. Runs
    /// the lookups concurrently and never throws — a failed lookup yields the default image.
    /// </summary>
    Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IArticleThumbnailResolver"/>
public class ArticleThumbnailResolver : IArticleThumbnailResolver
{
    /// <summary>Named <see cref="HttpClient"/> configured (in <c>Program.cs</c>) for scraping article pages.</summary>
    public const string HttpClientName = "ArticleImageScraper";

    private const string DefaultImagePath = "img/articles/default-img.png";
    private static readonly TimeSpan PerArticleTimeout = TimeSpan.FromSeconds(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArticleThumbnailResolver> _logger;

    public ArticleThumbnailResolver(IHttpClientFactory httpClientFactory, ILogger<ArticleThumbnailResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

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
        timeout.CancelAfter(PerArticleTimeout);

        try
        {
            var html = await client.GetStringAsync(articleUri, timeout.Token);

            var document = new HtmlDocument();
            document.LoadHtml(html);

            var ogImage = document.DocumentNode
                .SelectSingleNode("//meta[@property='og:image' or @name='og:image' or @property='twitter:image']")
                ?.GetAttributeValue("content", string.Empty);

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
