namespace FreedomBlaze.Data.Entities;

/// <summary>A single Bitcoin news article belonging to a <see cref="NewsDay"/>.</summary>
public class NewsArticle
{
    public int Id { get; set; }

    public int NewsDayId { get; set; }

    public NewsDay NewsDay { get; set; } = null!;

    /// <summary>Zero-based position within the day's set, preserving the generated ordering.</summary>
    public int Position { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string SourceRegion { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string ArticleUrl { get; set; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    public DateOnly PublishedDate { get; set; }
}
