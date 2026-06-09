namespace FreedomBlaze.Data.Entities;

/// <summary>
/// A persisted set of Bitcoin news for a single calendar day. This is the unit the date filter
/// uses to decide which dates are available for selection.
/// </summary>
public class NewsDay
{
    public int Id { get; set; }

    /// <summary>The calendar day this news set belongs to (unique).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The AI model that produced this set (e.g. "gpt-4o"), for traceability.</summary>
    public string? Model { get; set; }

    /// <summary>How many articles the response contained (denormalized for quick listing).</summary>
    public int ArticleCount { get; set; }

    /// <summary>When the set was first generated.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the set was last regenerated.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>The day's articles, ordered by <see cref="NewsArticle.Position"/>.</summary>
    public List<NewsArticle> Articles { get; set; } = [];
}
