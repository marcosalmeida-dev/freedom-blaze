using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FreedomBlaze.Client.Models;
using FreedomBlaze.Clients;
using FreedomBlaze.Constants;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNewsTests
{
    [Theory]
    [InlineData(429, "credit_balance_exhausted", "insufficient_quota", NewsFailureReason.QuotaExceeded, "quota")]
    [InlineData(429, "account_limit", "insufficient_quota", NewsFailureReason.QuotaExceeded, "quota")]
    [InlineData(429, "rate_limit_exceeded", "rate_limit_error", NewsFailureReason.RateLimited, "busy")]
    [InlineData(401, "invalid_api_key", "invalid_request_error", NewsFailureReason.Unauthorized, "credentials")]
    public async Task ProviderErrorsHaveActionableReasonsWithoutLeakingUpstreamDetails(
        int status, string code, string type, NewsFailureReason reason, string expectedMessage)
    {
        using var fixture = new NewsFixture(new DateOnly(2026, 1, 10),
            (_, _) => Task.FromResult(Error(status, code, type)));

        var exception = await Assert.ThrowsAsync<NewsUnavailableException>(
            () => fixture.Client.GetBitcoinNewsAsync(fixture.Today));

        Assert.Equal(reason, exception.Reason);
        Assert.Contains(expectedMessage, exception.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-only-detail", exception.UserMessage);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("incomplete", "cut short")]
    [InlineData("refusal", "declined")]
    public async Task IncompleteAndRefusedResponsesAreUnavailable(string responseKind, string expectedMessage)
    {
        var output = responseKind == "refusal"
            ? new object[] { Message(new { type = "refusal", refusal = "Cannot answer this request." }) }
            : [];
        using var fixture = new NewsFixture(new DateOnly(2026, 2, 10), (_, _) => Task.FromResult(
            JsonResponse(Response(output, responseKind == "incomplete" ? "incomplete" : "completed"))));

        var exception = await Assert.ThrowsAsync<NewsUnavailableException>(
            () => fixture.Client.GetBitcoinNewsAsync(fixture.Today));

        Assert.Equal(NewsFailureReason.Upstream, exception.Reason);
        Assert.Contains(expectedMessage, exception.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulResponseParsesArticlesAndRequiresLiveWebSearch()
    {
        var date = new DateOnly(2026, 3, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        var article = Assert.Single(articles);
        Assert.Equal("Bitcoin adoption expands", article.Title);
        Assert.Equal("A verified report about Bitcoin adoption.", article.Text);
        Assert.Equal("https://news.example/bitcoin-adoption", article.ArticleLinkUrl);
        Assert.Equal("Example News", article.Source);
        Assert.Equal("South America", article.SourceRegion);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), article.Date);

        using var request = JsonDocument.Parse(Assert.Single(fixture.Requests));
        var root = request.RootElement;
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal("web_search", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task CachedArticlesRemainReadableAfterRefreshFails()
    {
        var date = new DateOnly(2026, 4, 10);
        using var fixture = new NewsFixture(date,
            (_, _) => Task.FromResult(Error(429, "credit_balance_exhausted", "insufficient_quota")));
        var articles = new List<NewsArticleModel> { new() { Title = "Previously saved news" } };
        fixture.Cache.Set(CacheKeys.BitcoinNews(date), articles);

        var refresh = await fixture.Service.RefreshNewsAsync(date);
        var cached = await fixture.Service.GetNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, refresh.Status);
        Assert.Equal(NewsStatus.Ok, cached.Status);
        Assert.Same(articles, cached.Articles);
        Assert.Single(fixture.Requests);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Theory]
    [InlineData(429, "credit_balance_exhausted", "insufficient_quota", 5)]
    [InlineData(401, "invalid_api_key", "invalid_request_error", 6)]
    public async Task ProviderFailuresCoolDownAcrossDatesWhileStoredNewsRemainsReadable(
        int status, string code, string type, int month)
    {
        var date = new DateOnly(2026, month, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(Error(status, code, type)));
        var stored = new List<NewsArticleModel> { new() { Title = "Saved before the provider failed" } };
        fixture.Store.Days[date.AddDays(-2)] = stored;

        var initial = await fixture.Service.GetNewsAsync(date);
        var otherDate = await fixture.Service.GetNewsAsync(date.AddDays(-1));
        var savedDate = await fixture.Service.GetNewsAsync(date.AddDays(-2));

        Assert.Equal(NewsStatus.Unavailable, initial.Status);
        Assert.Equal(NewsStatus.Unavailable, otherDate.Status);
        Assert.Equal(initial.Message, otherDate.Message);
        Assert.Equal(NewsStatus.Ok, savedDate.Status);
        Assert.Same(stored, savedDate.Articles);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task OrdinaryRateLimitCoolDownOnlySuppressesTheFailingDate()
    {
        var date = new DateOnly(2026, 7, 10);
        using var fixture = new NewsFixture(date,
            (_, _) => Task.FromResult(Error(429, "rate_limit_exceeded", "rate_limit_error")));

        var first = await fixture.Service.GetNewsAsync(date);
        var sameDate = await fixture.Service.GetNewsAsync(date);
        Assert.Single(fixture.Requests);
        var otherDate = await fixture.Service.GetNewsAsync(date.AddDays(-1));

        Assert.Equal(NewsStatus.Unavailable, first.Status);
        Assert.Equal(first.Message, sameDate.Message);
        Assert.Equal(NewsStatus.Unavailable, otherDate.Status);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task GenerationTimeoutReturnsSpecificMessageAndDoesNotPersist()
    {
        using var fixture = new NewsFixture(new DateOnly(2026, 8, 10), async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The generation should have been canceled.");
        }, TimeSpan.FromMilliseconds(200));

        var result = await fixture.Service.GetNewsAsync(fixture.Today);

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Contains("took too long", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task MissingArticleLinkCanBeRecoveredFromACitationWithTheSameHeadline()
    {
        var date = new DateOnly(2026, 9, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(
            SuccessResponse(date, articleUrl: "", annotations: [Citation("Bitcoin adoption expands")]))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        var article = Assert.Single(articles);
        Assert.Equal("Bitcoin adoption expands", article.Title);
        Assert.Equal("https://news.example/cited-bitcoin-report", article.ArticleLinkUrl);
    }

    [Fact]
    public async Task MissingArticleLinkDoesNotReceiveAnUnrelatedCitationsUrl()
    {
        var date = new DateOnly(2026, 10, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(
            SuccessResponse(date, articleUrl: "", annotations: [Citation("A completely different story")]))));

        var exception = await Assert.ThrowsAsync<NewsUnavailableException>(
            () => fixture.Client.GetBitcoinNewsAsync(date));

        Assert.Equal(NewsFailureReason.Upstream, exception.Reason);
        Assert.Contains("No usable Bitcoin stories", exception.UserMessage);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameDateShareOneGenerationAndSave()
    {
        var date = new DateOnly(2026, 11, 10);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NewsFixture(date, async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return JsonResponse(SuccessResponse(date));
        });

        var first = fixture.Service.GetNewsAsync(date);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Service.GetNewsAsync(date);
        var secondWasWaiting = !second.IsCompleted;
        releaseResponse.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.True(secondWasWaiting);
        Assert.All(results, result => Assert.Equal(NewsStatus.Ok, result.Status));
        Assert.Same(results[0].Articles, results[1].Articles);
        Assert.Single(fixture.Requests);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal("Bitcoin adoption expands", Assert.Single(fixture.Store.Days[date]).Title);
    }

    private static HttpResponseMessage Error(int status, string code, string type) => JsonResponse(new
    {
        error = new { code, type, message = "provider-only-detail", param = (string?)null }
    }, (HttpStatusCode)status);

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static object Response(object[] output, string status = "completed") => new
    {
        id = "resp_news_test",
        @object = "response",
        created_at = 1_770_000_000,
        status,
        model = "gpt-5-mini",
        output,
        incomplete_details = status == "incomplete" ? new { reason = "max_output_tokens" } : null
    };

    private static object Message(object content) => new
    {
        type = "message",
        id = "msg_news_test",
        status = "completed",
        role = "assistant",
        content = new[] { content }
    };

    private static object Citation(string title) => new
    {
        type = "url_citation",
        start_index = 0,
        end_index = 24,
        url = "https://news.example/cited-bitcoin-report",
        title
    };

    private static object SuccessResponse(DateOnly date,
        string articleUrl = "https://news.example/bitcoin-adoption", object[]? annotations = null) => Response(
    [
        new
        {
            type = "web_search_call",
            id = "ws_news_test",
            status = "completed",
            action = new { type = "search", query = "Bitcoin news", sources = Array.Empty<object>() }
        },
        Message(new
        {
            type = "output_text",
            text = JsonSerializer.Serialize(new
            {
                articles = new[]
                {
                    new
                    {
                        title = "Bitcoin adoption expands",
                        summary = "A verified report about Bitcoin adoption.",
                        sourceName = "Example News",
                        sourceRegion = "South America",
                        sourceUrl = "https://news.example/",
                        articleUrl,
                        publishedDate = date.ToString("yyyy-MM-dd")
                    }
                }
            }),
            annotations = annotations ?? []
        })
    ]);

    private sealed class NewsFixture : IDisposable
    {
        private readonly HttpClient _httpClient;

        public NewsFixture(DateOnly today,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            TimeSpan? generationTimeout = null)
        {
            Today = today;
            var options = Microsoft.Extensions.Options.Options.Create(new OpenAiOptions
            {
                Model = "gpt-5-mini",
                GenerationTimeout = generationTimeout ?? TimeSpan.FromSeconds(10),
                FailureCooldown = TimeSpan.FromMinutes(1)
            });
            var clock = new FixedTimeProvider(today);
            _httpClient = new HttpClient(new StubHandler(Requests, respond));
            var sdk = new OpenAIClient(new ApiKeyCredential("test-key-never-sent-to-a-network"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://openai.invalid/v1"),
                Transport = new HttpClientPipelineTransport(_httpClient),
                RetryPolicy = new ClientRetryPolicy(0)
            });
            Client = new OpenAiNewsClient(options, clock, NullLogger<OpenAiNewsClient>.Instance, sdk);
            Service = new BitcoinNewsService(Client, Store, new NoThumbnails(), Cache, clock,
                options, NullLogger<BitcoinNewsService>.Instance);
        }

        public DateOnly Today { get; }
        public List<string> Requests { get; } = [];
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public StubStore Store { get; } = new();
        public OpenAiNewsClient Client { get; }
        public BitcoinNewsService Service { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            Cache.Dispose();
        }
    }

    private sealed class StubHandler(List<string> requests,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await respond(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class NoThumbnails : IArticleThumbnailHelper
    {
        public Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubStore : INewsStore
    {
        public Dictionary<DateOnly, List<NewsArticleModel>> Days { get; } = [];
        public int SaveCount { get; private set; }

        public Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
            => Task.FromResult(Days.GetValueOrDefault(date));

        public Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Days[date] = [.. articles];
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DateOnly>>([.. Days.Keys]);
    }
}
