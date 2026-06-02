using System.Net.Http.Json;
using FreedomBlaze.Models;

namespace FreedomBlaze.Client.Services;

/// <summary>
/// WebAssembly-side <see cref="IBitcoinNewsApi"/> implementation: reads Bitcoin news from the
/// server API over HTTP (against the browser origin).
/// </summary>
public class BitcoinNewsApiClient : IBitcoinNewsApi
{
    private readonly HttpClient _httpClient;

    public BitcoinNewsApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Gets today's Bitcoin news (served from the server-side daily cache).</summary>
    public async Task<List<NewsArticleModel>> GetNewsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _httpClient.GetFromJsonAsync<List<NewsArticleModel>>("api/bitcoin-news", cancellationToken);
        return result ?? [];
    }

    /// <summary>Requests a fresh web-search generation. May take several seconds.</summary>
    public async Task<List<NewsArticleModel>> RefreshNewsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync("api/bitcoin-news/refresh", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<List<NewsArticleModel>>(cancellationToken);
        return result ?? [];
    }
}
