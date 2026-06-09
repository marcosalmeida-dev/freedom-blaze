using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class NewsDayConfiguration : IEntityTypeConfiguration<NewsDay>
{
    public void Configure(EntityTypeBuilder<NewsDay> builder)
    {
        builder.ToTable("NewsDays");

        builder.HasKey(d => d.Id);

        // One news set per calendar day.
        builder.HasIndex(d => d.Date).IsUnique();
        builder.Property(d => d.Date).IsRequired();

        builder.Property(d => d.Model).HasMaxLength(64);

        builder.HasMany(d => d.Articles)
            .WithOne(a => a.NewsDay)
            .HasForeignKey(a => a.NewsDayId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
