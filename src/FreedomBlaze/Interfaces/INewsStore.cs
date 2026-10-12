using FreedomBlaze.Client.Models;

namespace FreedomBlaze.Interfaces;

/// <summary>
/// Persists the generated daily Bitcoin news set in the database so it survives application
/// restarts. Storage failures must propagate so callers can distinguish a saved edition
/// from a result available only in memory, and retry persistence without paying for research again.
/// </summary>
public interface INewsStore
{
    /// <summary>The stored news set for <paramref name="date"/>, or <c>null</c> if none exists.</summary>
    Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Last successful save time, used to coalesce the scheduled refresh across replicas.</summary>
    Task<DateTimeOffset?> GetUpdatedAtUtcAsync(DateOnly date, CancellationToken cancellationToken = default)
        => Task.FromResult<DateTimeOffset?>(null);

    /// <summary>
    /// Inserts or replaces the news set for <paramref name="date"/> — an existing day's articles are
    /// overwritten wholesale, so a refresh always persists the latest result.
    /// </summary>
    /// <param name="date">The calendar day the articles belong to.</param>
    /// <param name="articles">The generated articles to persist.</param>
    /// <param name="model">The AI model that produced the set, recorded as metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null, CancellationToken cancellationToken = default);

    /// <summary>The dates that currently have a saved news set, most recent first.</summary>
    Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default);
}
