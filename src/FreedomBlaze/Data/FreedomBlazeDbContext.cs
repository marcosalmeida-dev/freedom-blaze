using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Data;

/// <summary>
/// EF Core context for the application's local SQLite database (FreedomBlazeDB). Entity mappings
/// live in <c>Data/Configurations</c> and are applied via <see cref="OnModelCreating"/>.
/// </summary>
public class FreedomBlazeDbContext : DbContext
{
    public FreedomBlazeDbContext(DbContextOptions<FreedomBlazeDbContext> options) : base(options)
    {
    }

    public DbSet<NewsDay> NewsDays => Set<NewsDay>();

    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FreedomBlazeDbContext).Assembly);
    }
}
