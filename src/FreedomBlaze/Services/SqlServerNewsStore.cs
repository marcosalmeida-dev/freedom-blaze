using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Services;

/// <summary>
/// <see cref="INewsStore"/> backed by the SQL Server database (EF Core). The generated news
/// response is stored directly in the database — one <see cref="NewsDay"/> per calendar day owning
/// its ordered <see cref="NewsArticle"/> set — rather than as JSON files, and the day rows give the
/// date filter a cheap, authoritative list of which days actually have news.
/// <para>
/// Uses <see cref="IDbContextFactory{TContext}"/> directly (not the generic
/// <see cref="Data.Repositories.IRepository{TEntity}"/>) because replacing a day's articles is a
/// multi-entity unit of work that must run in a single context.
/// </para>
/// </summary>
public class SqlServerNewsStore : INewsStore
{
    private readonly IDbContextFactory<FreedomBlazeDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqlServerNewsStore> _logger;

    public SqlServerNewsStore(
        IDbContextFactory<FreedomBlazeDbContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<SqlServerNewsStore> logger)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var articles = await db.NewsArticles
            .AsNoTracking()
            .Where(a => a.NewsDay.Date == date)
            .OrderBy(a => a.Position)
            .ToListAsync(cancellationToken);

        return articles.Count == 0 ? null : articles.Select(a => a.ToDto()).ToList();
    }

    public async Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null, CancellationToken cancellationToken = default)
    {
        if (articles.Count == 0)
        {
            return;
        }

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var day = await db.NewsDays
                .Include(d => d.Articles)
                .FirstOrDefaultAsync(d => d.Date == date, cancellationToken);

            var now = _timeProvider.GetUtcNow();

            if (day is null)
            {
                day = new NewsDay { Date = date, CreatedAtUtc = now };
                db.NewsDays.Add(day);
            }
            else
            {
                // Replace the day's articles wholesale (cascade handles the deletes).
                db.NewsArticles.RemoveRange(day.Articles);
                day.Articles.Clear();
            }

            day.Model = model;
            day.UpdatedAtUtc = now;
            day.ArticleCount = articles.Count;
            for (var i = 0; i < articles.Count; i++)
            {
                day.Articles.Add(articles[i].ToEntity(i));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist Bitcoin news to the database for {Date}.", date);
        }
    }

    public async Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.NewsDays
            .AsNoTracking()
            .Where(d => d.ArticleCount > 0)
            .OrderByDescending(d => d.Date)
            .Select(d => d.Date)
            .ToListAsync(cancellationToken);
    }
}
