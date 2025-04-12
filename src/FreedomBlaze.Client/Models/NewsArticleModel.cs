namespace FreedomBlaze.Models;

public class NewsArticleModel
{
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ArticleLinkUrl { get; set; } = string.Empty;
    public string? NewsThumbImg { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceRegion { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}
