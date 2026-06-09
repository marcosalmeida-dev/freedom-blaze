using FreedomBlaze.Models;

namespace FreedomBlaze.Services;

/// <summary>
/// Persists the generated daily Bitcoin news set in the database so it survives application
/// restarts. Implementations are best-effort: <see cref="LoadAsync"/> returns <c>null</c> when
/// nothing is stored or on error, and <see cref="SaveAsync"/> never throws.
/// </summary>
public interface INewsStore
{
    /// <summary>The stored news set for <paramref name="date"/>, or <c>null</c> if none exists.</summary>
    Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces the news set for <paramref name="date"/> — an existing day's articles are
    /// overwritten wholesale, so a refresh always persists the latest result.
    /// </summary>
    /// <param name="model">The AI model that produced the set, recorded as metadata.</param>
    Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null, CancellationToken cancellationToken = default);

    /// <summary>The dates that currently have a saved news set, most recent first.</summary>
    Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default);
}
