using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models;

namespace FreedomBlaze.Data;

/// <summary>
/// Maps between the persistence entity (<see cref="NewsArticle"/>) and the wire/UI DTO
/// (<see cref="NewsArticleModel"/>). Keeping the two separate lets the storage schema and the
/// transport contract evolve independently.
/// </summary>
internal static class NewsMapping
{
    public static NewsArticleModel ToDto(this NewsArticle entity) => new()
    {
        Title = entity.Title,
        Text = entity.Summary,
        ArticleLinkUrl = entity.ArticleUrl,
        NewsThumbImg = entity.ThumbnailUrl,
        Source = entity.Source,
        SourceRegion = entity.SourceRegion,
        SourceUrl = entity.SourceUrl,
        Date = entity.PublishedDate.ToDateTime(TimeOnly.MinValue),
    };

    public static NewsArticle ToEntity(this NewsArticleModel dto, int position) => new()
    {
        Position = position,
        Title = dto.Title,
        Summary = dto.Text,
        ArticleUrl = dto.ArticleLinkUrl,
        ThumbnailUrl = dto.NewsThumbImg,
        Source = dto.Source,
        SourceRegion = dto.SourceRegion,
        SourceUrl = dto.SourceUrl,
        PublishedDate = DateOnly.FromDateTime(dto.Date),
    };
}
