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
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task SuccessfulResponseParsesArticlesAndRequiresLiveWebSearch()
    {
        var date = new DateOnly(2026, 3, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        var article = articles[0];
        Assert.Equal("Bitcoin adoption expands", article.Title);
        Assert.Equal("A verified report about Bitcoin adoption.", article.Text);
        Assert.Equal("https://portaldobitcoin.uol.com.br/bitcoin-adoption", article.ArticleLinkUrl);
        Assert.Equal("Portal do Bitcoin", article.Source);
        Assert.Equal("Brazil", article.SourceRegion);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), article.Date);

        using var request = JsonDocument.Parse(Assert.Single(fixture.Requests));
        var root = request.RootElement;
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal("web_search", root.GetProperty("tools")[0].GetProperty("type").GetString());
        var allowedDomains = root.GetProperty("tools")[0].GetProperty("filters").GetProperty("allowed_domains")
            .EnumerateArray().Select(domain => domain.GetString()).ToList();
        Assert.All(BalancedStories(date), story => Assert.Contains(new Uri(story.ArticleUrl).Host, allowedDomains));
        Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        var articlesSchema = root.GetProperty("text").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("articles");
        Assert.Equal(new[] { "array", "null" }, articlesSchema.GetProperty("type").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal(9, articlesSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(9, articlesSchema.GetProperty("maxItems").GetInt32());
        var articleProperties = articlesSchema.GetProperty("items").GetProperty("properties");
        Assert.Equal(6, articleProperties.GetProperty("continent").GetProperty("enum").GetArrayLength());
        Assert.True(articleProperties.TryGetProperty("countryCode", out _));
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

        Assert.Equal(9, articles.Count);
        var article = articles[0];
        Assert.Equal("Bitcoin adoption expands", article.Title);
        Assert.Equal("https://portaldobitcoin.uol.com.br/cited-bitcoin-report", article.ArticleLinkUrl);
        Assert.Single(fixture.Requests);
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
        Assert.Equal(2, fixture.Requests.Count);
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
        Assert.Equal(9, fixture.Store.Days[date].Count);
        Assert.Equal("Bitcoin adoption expands", fixture.Store.Days[date][0].Title);
    }

    [Theory]
    [InlineData("missing-continent")]
    [InlineData("missing-brazil")]
    [InlineData("too-few")]
    [InlineData("too-many")]
    [InlineData("duplicate-link")]
    [InlineData("duplicate-link-tracking")]
    [InlineData("duplicate-link-www")]
    [InlineData("duplicate-link-publisher-alias")]
    [InlineData("north-america-heavy")]
    [InlineData("unapproved-publisher")]
    [InlineData("deceptive-publisher-host")]
    [InlineData("invalid-country")]
    [InlineData("misclassified-us")]
    [InlineData("misclassified-japan")]
    [InlineData("old-date")]
    [InlineData("future-date")]
    [InlineData("invalid-date")]
    [InlineData("missing-date")]
    [InlineData("null-edition")]
    public async Task InvalidCoverageIsRepairedOnceAndOnlyTheCorrectedNineStoriesAreReturned(string defect)
    {
        var date = new DateOnly(2026, 12, 10);
        var callCount = 0;
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(
            ++callCount == 1 ? StoriesResponse(InvalidStories(date, defect)) : SuccessResponse(date))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        Assert.Equal(BalancedStories(date).Select(story => story.ArticleUrl), articles.Select(article => article.ArticleLinkUrl));
        Assert.Equal(2, fixture.Requests.Count);
        Assert.NotEqual(fixture.Requests[0], fixture.Requests[1]);
        using var repair = JsonDocument.Parse(fixture.Requests[1]);
        Assert.Equal("required", repair.RootElement.GetProperty("tool_choice").GetString());
    }

    [Theory]
    [InlineData("missing-continent")]
    [InlineData("missing-brazil")]
    [InlineData("too-few")]
    [InlineData("null-edition")]
    public async Task InvalidCoverageAfterRepairThrowsWithoutAnotherGeneration(string defect)
    {
        var date = new DateOnly(2027, 1, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(
            StoriesResponse(InvalidStories(date, defect)))));

        var exception = await Assert.ThrowsAsync<NewsUnavailableException>(
            () => fixture.Client.GetBitcoinNewsAsync(date));

        Assert.Equal(NewsFailureReason.Upstream, exception.Reason);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task ServiceDoesNotCacheOrPersistAnUnbalancedSetAfterBothSearchesFailCoverage()
    {
        var date = new DateOnly(2027, 2, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(
            StoriesResponse(InvalidStories(date, "missing-continent")))));

        var result = await fixture.Service.GetNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Empty(result.Articles);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Store.Days);
        Assert.False(fixture.Cache.TryGetValue(CacheKeys.BitcoinNews(date), out _));
    }

    [Theory]
    [InlineData(6, "https://coinpost.jp/?p=12345")]
    [InlineData(5, "https://bitcoinke.io/?p=6789")]
    public async Task SupportedQueryArticleLinksAreUsableWithoutRepair(int storyIndex, string articleUrl)
    {
        var date = new DateOnly(2027, 3, 10);
        var stories = BalancedStories(date);
        stories[storyIndex] = stories[storyIndex] with { ArticleUrl = articleUrl };
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(StoriesResponse(stories))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        Assert.Equal(articleUrl, articles[storyIndex].ArticleLinkUrl);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task DisplayCountryIsNormalizedFromCountryCode()
    {
        var date = new DateOnly(2027, 4, 10);
        var stories = BalancedStories(date);
        stories[0] = stories[0] with { SourceRegion = "United States" };
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(StoriesResponse(stories))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        Assert.Equal("Brazil", articles[0].SourceRegion);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task TodaysEditionCanContainStoriesPublishedYesterday()
    {
        var today = new DateOnly(2027, 5, 10);
        var yesterday = today.AddDays(-1);
        using var fixture = new NewsFixture(today, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(yesterday))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(today);

        Assert.Equal(9, articles.Count);
        Assert.All(articles, article => Assert.Equal(yesterday.ToDateTime(TimeOnly.MinValue), article.Date));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task HistoricalEditionRepairsStoriesFromOutsideTheRequestedDate()
    {
        var today = new DateOnly(2027, 6, 10);
        var requestedDate = today.AddDays(-5);
        var callCount = 0;
        using var fixture = new NewsFixture(today, (_, _) => Task.FromResult(JsonResponse(
            SuccessResponse(++callCount == 1 ? requestedDate.AddDays(-1) : requestedDate))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(requestedDate);

        Assert.Equal(9, articles.Count);
        Assert.All(articles, article => Assert.Equal(requestedDate.ToDateTime(TimeOnly.MinValue), article.Date));
        Assert.Equal(2, fixture.Requests.Count);
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
        url = "https://portaldobitcoin.uol.com.br/cited-bitcoin-report",
        title
    };

    private static object SuccessResponse(DateOnly date,
        string articleUrl = "https://portaldobitcoin.uol.com.br/bitcoin-adoption", object[]? annotations = null)
    {
        var stories = BalancedStories(date);
        stories[0] = stories[0] with { ArticleUrl = articleUrl };
        return StoriesResponse(stories, annotations);
    }

    private static object StoriesResponse(IReadOnlyList<NewsStory>? stories, object[]? annotations = null) => Response(
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
            text = JsonSerializer.Serialize(new { articles = stories }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            annotations = annotations ?? []
        })
    ]);

    private static List<NewsStory> BalancedStories(DateOnly date) =>
    [
        new("Bitcoin adoption expands", "A verified report about Bitcoin adoption.", "Portal do Bitcoin", "Brazil",
            "https://portaldobitcoin.uol.com.br/", "https://portaldobitcoin.uol.com.br/bitcoin-adoption", date.ToString("yyyy-MM-dd"), "South America", "BR"),
        Story(date, "American Bitcoin payments", "CoinDesk", "United States", "coindesk.com", "North America", "US"),
        Story(date, "Canadian Bitcoin mining", "Bitcoin Magazine", "Canada", "bitcoinmagazine.com", "North America", "CA"),
        Story(date, "German Bitcoin regulation", "Cointelegraph", "Germany", "cointelegraph.com", "Europe", "DE"),
        Story(date, "French Bitcoin custody", "Decrypt", "France", "decrypt.co", "Europe", "FR"),
        Story(date, "Kenyan Bitcoin remittances", "BitcoinKE", "Kenya", "bitcoinke.io", "Africa", "KE"),
        Story(date, "Japanese Bitcoin exchange", "CoinPost", "Japan", "coinpost.jp", "Asia", "JP"),
        Story(date, "Singapore Bitcoin infrastructure", "Blockworks", "Singapore", "blockworks.co", "Asia", "SG"),
        Story(date, "Australian Bitcoin adoption", "Crypto News Australia", "Australia", "cryptonews.com.au", "Oceania", "AU")
    ];

    private static NewsStory Story(DateOnly date, string title, string source, string country, string domain, string continent, string countryCode)
        => new(title, "A verified Bitcoin report.", source, country, $"https://{domain}/",
            $"https://{domain}/bitcoin-{countryCode.ToLowerInvariant()}", date.ToString("yyyy-MM-dd"), continent, countryCode);

    private static List<NewsStory>? InvalidStories(DateOnly date, string defect)
    {
        var stories = BalancedStories(date);
        switch (defect)
        {
            case "null-edition":
                return null;
            case "missing-continent":
                stories[8] = stories[8] with { Continent = "Europe", CountryCode = "ES", SourceRegion = "Spain" };
                break;
            case "missing-brazil":
                stories[0] = stories[0] with { CountryCode = "AR", SourceRegion = "Argentina" };
                break;
            case "too-few":
                stories.RemoveAt(4);
                break;
            case "too-many":
                stories.Add(Story(date, "Chilean Bitcoin savings", "Bitcoin.com", "Chile", "news.bitcoin.com", "South America", "CL"));
                break;
            case "duplicate-link":
                stories[4] = stories[4] with { ArticleUrl = stories[3].ArticleUrl };
                break;
            case "duplicate-link-tracking":
                stories[4] = stories[4] with { ArticleUrl = stories[3].ArticleUrl + "?utm_source=duplicate" };
                break;
            case "duplicate-link-www":
                stories[4] = stories[4] with { ArticleUrl = stories[3].ArticleUrl.Replace("https://", "https://www.") };
                break;
            case "duplicate-link-publisher-alias":
                stories[4] = stories[4] with { ArticleUrl = stories[7].ArticleUrl.Replace("blockworks.co/", "blockworks.com/") };
                break;
            case "north-america-heavy":
                stories[4] = stories[4] with { Continent = "North America", CountryCode = "US", SourceRegion = "United States" };
                break;
            case "unapproved-publisher":
                stories[4] = stories[4] with { ArticleUrl = "https://unknown.example/bitcoin" };
                break;
            case "deceptive-publisher-host":
                stories[4] = stories[4] with { ArticleUrl = "https://coindesk.com.evil.example/bitcoin" };
                break;
            case "invalid-country":
                stories[4] = stories[4] with { CountryCode = "ZZ" };
                break;
            case "misclassified-us":
                stories[5] = stories[5] with { CountryCode = "US", SourceRegion = "United States" };
                break;
            case "misclassified-japan":
                stories[6] = stories[6] with { Continent = "Africa" };
                break;
            case "old-date":
                stories[4] = stories[4] with { PublishedDate = date.AddDays(-2).ToString("yyyy-MM-dd") };
                break;
            case "future-date":
                stories[4] = stories[4] with { PublishedDate = date.AddDays(1).ToString("yyyy-MM-dd") };
                break;
            case "invalid-date":
                stories[4] = stories[4] with { PublishedDate = "not-a-date" };
                break;
            case "missing-date":
                stories[4] = stories[4] with { PublishedDate = null };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown fixture defect.");
        }

        return stories;
    }

    private sealed record NewsStory(string Title, string Summary, string SourceName, string SourceRegion,
        string SourceUrl, string ArticleUrl, string? PublishedDate, string Continent, string CountryCode);

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
