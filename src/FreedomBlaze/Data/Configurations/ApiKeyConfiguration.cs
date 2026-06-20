using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.Name).IsRequired().HasMaxLength(128);

        // SHA-256 hex is always 64 chars; the unique index makes authentication a single indexed lookup.
        builder.Property(k => k.KeyHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(k => k.KeyHash).IsUnique();

        builder.Property(k => k.Prefix).IsRequired().HasMaxLength(32);
    }
}
