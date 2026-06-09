using FreedomBlaze.Client.Models;
using FreedomBlaze.Interfaces;
using HtmlAgilityPack;

namespace FreedomBlaze.Helpers;

/// <inheritdoc cref="Interfaces.IArticleThumbnailHelper"/>
public class ArticleThumbnailHelper(IHttpClientFactory httpClientFactory, ILogger<ArticleThumbnailHelper> logger)
    : Interfaces.IArticleThumbnailHelper
{
    /// <summary>Named <see cref="HttpClient"/> configured (in <c>Program.cs</c>) for scraping article pages.</summary>
    public const string HttpClientName = "ArticleImageScraper";

    private const string DefaultImagePath = "img/articles/default-img.png";
    private static readonly TimeSpan PerArticleTimeout = TimeSpan.FromSeconds(6);

    public async Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

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
            logger.LogDebug(ex, "Could not fetch Open Graph image for {Url}.", articleUrl);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error while parsing article {Url}.", articleUrl);
            return null;
        }
    }
}
