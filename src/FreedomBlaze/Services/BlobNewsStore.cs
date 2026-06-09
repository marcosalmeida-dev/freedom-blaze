using System.Globalization;
using System.Text.Json;
using FreedomBlaze.Models;

namespace FreedomBlaze.Services;

/// <summary>
/// Persists the daily news set to Azure Blob Storage. Opt-in: selected by
/// "NewsStorage:Provider=AzureBlob" and requires a configured "BlobStorage" account.
/// </summary>
public class BlobNewsStore : INewsStore
{
    private const string ContainerName = "bitcoin-news";

    private readonly BlobStorageService _blobStorage;
    private readonly ILogger<BlobNewsStore> _logger;

    public BlobNewsStore(BlobStorageService blobStorage, ILogger<BlobNewsStore> logger)
    {
        _blobStorage = blobStorage;
        _logger = logger;
    }

    public async Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _blobStorage.DownloadTextAsync(ContainerName, NewsStoreConventions.EntryName(date));
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<List<NewsArticleModel>>(json, NewsStoreConventions.JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Bitcoin news from blob storage for {Date}.", date);
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
            var json = JsonSerializer.Serialize(articles, NewsStoreConventions.JsonOptions);
            await _blobStorage.UploadTextAsync(ContainerName, NewsStoreConventions.EntryName(date), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist Bitcoin news to blob storage for {Date}.", date);
        }
    }

    public async Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var names = await _blobStorage.ListBlobNamesAsync(ContainerName, cancellationToken);

            var dates = new List<DateOnly>();
            foreach (var name in names)
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                if (DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    dates.Add(date);
                }
            }

            dates.Sort();
            dates.Reverse();
            return dates;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Bitcoin news dates from blob storage.");
            return [];
        }
    }
}
