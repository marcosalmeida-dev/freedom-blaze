using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Data;

/// <summary>
/// EF Core context for the application's SQL Server database (FreedomBlazeDb). Entity mappings
/// live in <c>Data/Configurations</c> and are applied via <see cref="OnModelCreating"/>; the schema
/// is created/evolved through code-first migrations in <c>Data/Migrations</c>.
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
