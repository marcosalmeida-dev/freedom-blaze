using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class NewsArticleConfiguration : IEntityTypeConfiguration<NewsArticle>
{
    public void Configure(EntityTypeBuilder<NewsArticle> builder)
    {
        builder.ToTable("NewsArticles");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Title).IsRequired().HasMaxLength(512);
        builder.Property(a => a.Summary).HasMaxLength(4000);
        builder.Property(a => a.Source).HasMaxLength(256);
        builder.Property(a => a.SourceRegion).HasMaxLength(128);
        builder.Property(a => a.SourceUrl).HasMaxLength(2048);
        builder.Property(a => a.ArticleUrl).HasMaxLength(2048);
        builder.Property(a => a.ThumbnailUrl).HasMaxLength(2048);

        // Fast ordered retrieval of a day's articles.
        builder.HasIndex(a => new { a.NewsDayId, a.Position });
    }
}
