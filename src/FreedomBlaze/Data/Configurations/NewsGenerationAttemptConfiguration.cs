using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class NewsGenerationAttemptConfiguration : IEntityTypeConfiguration<NewsGenerationAttempt>
{
    public void Configure(EntityTypeBuilder<NewsGenerationAttempt> builder)
    {
        builder.ToTable("NewsGenerationAttempts");
        builder.HasKey(attempt => attempt.Date);
        builder.Property(attempt => attempt.Date).HasColumnType("date").ValueGeneratedNever();
        builder.Property(attempt => attempt.LastFailure).HasMaxLength(512);
    }
}
