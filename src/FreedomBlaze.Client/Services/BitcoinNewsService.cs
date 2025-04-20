using System.Net.Http;
using System.Net.Http.Json;
using FreedomBlaze.Models;

namespace FreedomBlaze.Client.Services;

public class BitcoinNewsService
{
    private readonly HttpClient _httpClient;
    public BitcoinNewsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<NewsArticleModel>> GetRefrehedBitcoinNews(string model)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await _httpClient.GetFromJsonAsync<List<NewsArticleModel>>($"api/bitcoin-news/get-chatgpt-news?model={model}", cancellationToken: cts.Token);
        return result ?? new List<NewsArticleModel>();
    }

    public async Task<List<NewsArticleModel>> GetBtcNews()
    {
        var result = await _httpClient.GetFromJsonAsync<List<NewsArticleModel>>("api/bitcoin-news");

        return result ?? new List<NewsArticleModel>();
    }
}
