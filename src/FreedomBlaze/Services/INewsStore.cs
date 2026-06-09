using System.Text.Json;
using FreedomBlaze.Models;

namespace FreedomBlaze.Services;

/// <summary>
/// Persists the generated daily Bitcoin news set so it survives application restarts.
/// Implementations are best-effort: <see cref="LoadAsync"/> returns <c>null</c> when nothing is
/// stored or on error, and <see cref="SaveAsync"/> never throws.
/// </summary>
public interface INewsStore
{
    Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <param name="model">The AI model that produced the set, recorded as metadata where supported.</param>
    Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null, CancellationToken cancellationToken = default);

    /// <summary>The dates that currently have a saved news set, most recent first.</summary>
    Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Shared serialization and naming conventions for <see cref="INewsStore"/> backends.</summary>
internal static class NewsStoreConventions
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>One file/blob per calendar day, e.g. "2026-06-01.json".</summary>
    public static string EntryName(DateOnly date) => $"{date:yyyy-MM-dd}.json";
}
