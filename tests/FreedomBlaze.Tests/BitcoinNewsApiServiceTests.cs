using System.Net;
using System.Text;
using FreedomBlaze.Client.Models;
using FreedomBlaze.Client.Services;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNewsApiServiceTests
{
    [Fact]
    public async Task ProviderProblemDetailsReachTheReader()
    {
        using var http = CreateClient(HttpStatusCode.ServiceUnavailable,
            """{"detail":"Bitcoin news is paused because the news provider quota has run out."}""");
        var service = new BitcoinNewsApiService(http);

        var result = await service.GetNewsAsync(new DateOnly(2026, 9, 18));

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Contains("quota has run out", result.Message);
        Assert.Empty(result.Articles);
    }

    [Fact]
    public async Task MalformedSuccessfulResponseIsUnavailableRatherThanEmptyNews()
    {
        using var http = CreateClient(HttpStatusCode.OK, "<html>Proxy error</html>");
        var service = new BitcoinNewsApiService(http);

        var result = await service.GetNewsAsync(new DateOnly(2026, 9, 18));

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Empty(await service.GetAvailableDatesAsync());
    }

    [Fact]
    public async Task EmptySuccessfulResponseIsAnEmptyDay()
    {
        using var http = CreateClient(HttpStatusCode.OK, "[]");
        var service = new BitcoinNewsApiService(http);

        var result = await service.GetNewsAsync(new DateOnly(2026, 9, 18));

        Assert.Equal(NewsStatus.Empty, result.Status);
        Assert.Null(result.Message);
    }

    private static HttpClient CreateClient(HttpStatusCode status, string body) =>
        new(new ResponseHandler(status, body)) { BaseAddress = new Uri("https://news.invalid/") };

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
