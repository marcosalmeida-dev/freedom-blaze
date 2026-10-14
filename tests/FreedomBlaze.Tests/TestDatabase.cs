using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Tests;

/// <summary>
/// The app's schema in a private in-memory SQLite database that lives as long as this object. Foreign
/// keys and the unique (filtered) indexes are enforced, so ownership and idempotency races behave as
/// they do on SQL Server.
/// </summary>
internal sealed class TestDatabase : IDbContextFactory<FreedomBlazeDbContext>, IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<FreedomBlazeDbContext> _options;
    private int _keys;

    public TestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<FreedomBlazeDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public FreedomBlazeDbContext CreateDbContext() => new(_options);

    public IRepository<TEntity> Repository<TEntity>() where TEntity : class => new EfRepository<TEntity>(this);

    public Merchant AddMerchant(string name = "Artcana", bool isActive = true) => Add(new Merchant
    {
        Name = name,
        IsActive = isActive,
        CreatedAtUtc = Created,
        DeactivatedAtUtc = isActive ? null : Created,
    });

    /// <summary>A key row to own invoices; its hash is not a real secret's, so it cannot authenticate.</summary>
    public ApiKey AddKey(int? merchantId, ApiKeyScopes scopes = ApiKeyScopes.All)
    {
        var n = ++_keys;
        return Add(new ApiKey
        {
            Name = $"key {n}",
            KeyHash = n.ToString("x64"),
            Prefix = $"fb_live_test{n}",
            MerchantId = merchantId,
            Scopes = scopes,
            CreatedAtUtc = Created,
        });
    }

    public TEntity Add<TEntity>(TEntity entity) where TEntity : class
    {
        using var db = CreateDbContext();
        db.Add(entity);
        db.SaveChanges();
        return entity;
    }

    public List<LightningInvoice> Invoices()
    {
        using var db = CreateDbContext();
        return db.LightningInvoices.AsNoTracking().OrderBy(i => i.Id).ToList();
    }

    public void Dispose() => _connection.Dispose();
}
