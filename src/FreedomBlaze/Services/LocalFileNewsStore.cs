using System.Globalization;
using System.Text.Json;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Persists the daily news set as JSON files on the local filesystem. This is the default
/// backend and needs no external infrastructure.
/// </summary>
public class LocalFileNewsStore : INewsStore
{
    private readonly string _directory;
    private readonly ILogger<LocalFileNewsStore> _logger;

    public LocalFileNewsStore(IOptions<NewsStorageOptions> options, IHostEnvironment environment, ILogger<LocalFileNewsStore> logger)
    {
        _logger = logger;

        var configured = options.Value.LocalPath;
        _directory = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
    }

    public async Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, NewsStoreConventions.EntryName(date));
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<NewsArticleModel>>(
                stream, NewsStoreConventions.JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local news file {Path}.", path);
            return null;
        }
    }

    public async Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null, CancellationToken cancellationToken = default)
    {
        if (articles.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, NewsStoreConventions.EntryName(date));

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, articles, NewsStoreConventions.JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write local news file for {Date}.", date);
        }
    }

    public Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return Task.FromResult<IReadOnlyList<DateOnly>>([]);
        }

        var dates = new List<DateOnly>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
        }

        dates.Sort();
        dates.Reverse();
        return Task.FromResult<IReadOnlyList<DateOnly>>(dates);
    }
}
