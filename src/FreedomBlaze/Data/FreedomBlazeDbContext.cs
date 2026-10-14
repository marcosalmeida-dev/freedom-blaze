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

    public DbSet<NewsGenerationAttempt> NewsGenerationAttempts => Set<NewsGenerationAttempt>();

    public DbSet<Donation> Donations => Set<Donation>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Merchant> Merchants => Set<Merchant>();

    public DbSet<LightningInvoice> LightningInvoices => Set<LightningInvoice>();

    public DbSet<LightningBackend> LightningBackends => Set<LightningBackend>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FreedomBlazeDbContext).Assembly);
    }
}
