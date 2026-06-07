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

    /// <summary>Gets the Bitcoin news for the given date (served from the server-side daily cache/store).</summary>
    public async Task<List<NewsArticleModel>> GetNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var result = await _httpClient.GetFromJsonAsync<List<NewsArticleModel>>(
            $"api/bitcoin-news?date={date:yyyy-MM-dd}", cancellationToken);
        return result ?? [];
    }

    /// <summary>Requests a fresh web-search generation for the given date. May take several seconds.</summary>
    public async Task<List<NewsArticleModel>> RefreshNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/bitcoin-news/refresh?date={date:yyyy-MM-dd}", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<List<NewsArticleModel>>(cancellationToken);
        return result ?? [];
    }
}
