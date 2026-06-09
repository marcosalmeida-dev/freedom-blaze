using FreedomBlaze.Client.Services;
using FreedomBlaze.Models;

namespace FreedomBlaze.Services;

/// <summary>
/// Server-side <see cref="IBitcoinNewsApi"/> implementation. When the news component renders on
/// the server (prerender or InteractiveServer), it resolves this instead of the HTTP client, so
/// it calls <see cref="BitcoinNewsService"/> in-process — no loopback request, no dependency on a
/// configured base URL.
/// </summary>
public class ServerBitcoinNewsApi : IBitcoinNewsApi
{
    private readonly BitcoinNewsService _newsService;

    public ServerBitcoinNewsApi(BitcoinNewsService newsService)
    {
        _newsService = newsService;
    }

    public async Task<List<NewsArticleModel>> GetNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var news = await _newsService.GetNewsForDateAsync(date, cancellationToken);
        return [.. news];
    }

    public async Task<List<NewsArticleModel>> RefreshNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var news = await _newsService.RefreshNewsForDateAsync(date, cancellationToken);
        return [.. news];
    }

    public async Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
    {
        var dates = await _newsService.GetAvailableDatesAsync(cancellationToken);
        return [.. dates];
    }
}
