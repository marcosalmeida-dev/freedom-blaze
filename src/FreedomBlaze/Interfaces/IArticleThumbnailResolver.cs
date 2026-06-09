using FreedomBlaze.Models;

namespace FreedomBlaze.Interfaces;

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
