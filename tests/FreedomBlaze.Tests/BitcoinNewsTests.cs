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
using Microsoft.Extensions.Logging;
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
        var article = Assert.Single(articles, article => article.Title == "Bitcoin adoption expands");
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
        Assert.Equal("array", articlesSchema.GetProperty("type").GetString());
        Assert.Equal(0, articlesSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(18, articlesSchema.GetProperty("maxItems").GetInt32());
        Assert.Equal("string", root.GetProperty("text").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("missingCoverage").GetProperty("type").GetString());
        var articleProperties = articlesSchema.GetProperty("items").GetProperty("properties");
        Assert.Equal(6, articleProperties.GetProperty("continent").GetProperty("enum").GetArrayLength());
        Assert.True(articleProperties.TryGetProperty("countryCode", out _));
    }

    [Fact]
    public async Task DefaultModelUsesMediumReasoningAndTheConfiguredOutputBudget()
    {
        var date = new DateOnly(2027, 10, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            settings: new OpenAiOptions());

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        using var request = JsonDocument.Parse(Assert.Single(fixture.Requests));
        Assert.Equal("gpt-6.1-sol", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("medium", request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(24_000, request.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(8, request.RootElement.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal("medium", request.RootElement.GetProperty("tools")[0].GetProperty("search_context_size").GetString());
        var metadata = request.RootElement.GetProperty("metadata");
        Assert.Equal("freedomblaze_bitcoin_news", metadata.GetProperty("application").GetString());
        Assert.Equal(date.ToString("yyyy-MM-dd"), metadata.GetProperty("edition_date").GetString());
        Assert.Equal("1", metadata.GetProperty("news_attempt").GetString());
        Assert.True(Guid.TryParse(metadata.GetProperty("news_job_id").GetString(), out _));
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

        var initial = await fixture.Service.EnsureNewsAsync(date);
        var otherDate = await fixture.Service.EnsureNewsAsync(date.AddDays(-1));
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

        var first = await fixture.Service.EnsureNewsAsync(date);
        var sameDate = await fixture.Service.EnsureNewsAsync(date);
        Assert.Single(fixture.Requests);
        var otherDate = await fixture.Service.EnsureNewsAsync(date.AddDays(-1));

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

        var result = await fixture.Service.EnsureNewsAsync(fixture.Today);

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
        var article = Assert.Single(articles, article => article.Title == "Bitcoin adoption expands");
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

        var first = fixture.Service.EnsureNewsAsync(date);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Service.EnsureNewsAsync(date);
        var secondWasWaiting = !second.IsCompleted;
        var reader = fixture.Service.GetNewsAsync(date);
        var readerCompletedBeforeGeneration = reader.IsCompletedSuccessfully;
        releaseResponse.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.True(secondWasWaiting);
        Assert.True(readerCompletedBeforeGeneration);
        Assert.Equal(NewsStatus.Empty, (await reader).Status);
        Assert.All(results, result => Assert.Equal(NewsStatus.Ok, result.Status));
        Assert.Same(results[0].Articles, results[1].Articles);
        Assert.Single(fixture.Requests);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(9, fixture.Store.Days[date].Count);
        Assert.Contains(fixture.Store.Days[date], article => article.Title == "Bitcoin adoption expands");
    }

    [Theory]
    [InlineData("missing-continent")]
    [InlineData("missing-brazil")]
    [InlineData("too-few")]
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
        Assert.Equal(BalancedStories(date).Select(story => story.ArticleUrl).Order(StringComparer.Ordinal),
            articles.Select(article => article.ArticleLinkUrl).Order(StringComparer.Ordinal));
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

        var result = await fixture.Service.EnsureNewsAsync(date);

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
        Assert.Contains(articles, article => article.ArticleLinkUrl == articleUrl);
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
        Assert.Equal("Brazil", Assert.Single(articles, article => article.Title == "Bitcoin adoption expands").SourceRegion);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TodaysEditionAcceptsAllThreeCalendarDatesInItsPublicationWindow(int daysAgo)
    {
        var today = new DateOnly(2027, 5, 10);
        var publicationDate = today.AddDays(-daysAgo);
        using var fixture = new NewsFixture(today, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(publicationDate))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(today);

        Assert.Equal(9, articles.Count);
        Assert.All(articles, article => Assert.Equal(publicationDate.ToDateTime(TimeOnly.MinValue), article.Date));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task HistoricalEditionRepairsStoriesFromOutsideItsPublicationWindow()
    {
        var today = new DateOnly(2027, 6, 10);
        var requestedDate = today.AddDays(-5);
        var callCount = 0;
        using var fixture = new NewsFixture(today, (_, _) => Task.FromResult(JsonResponse(
            SuccessResponse(++callCount == 1 ? requestedDate.AddDays(-3) : requestedDate))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(requestedDate);

        Assert.Equal(9, articles.Count);
        Assert.All(articles, article => Assert.Equal(requestedDate.ToDateTime(TimeOnly.MinValue), article.Date));
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task HistoricalEditionUsesTheSameThreeCalendarDateWindow()
    {
        var today = new DateOnly(2027, 6, 15);
        var requestedDate = today.AddDays(-5);
        var publicationDate = requestedDate.AddDays(-2);
        using var fixture = new NewsFixture(today, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(publicationDate))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(requestedDate);

        Assert.Equal(9, articles.Count);
        Assert.All(articles, article => Assert.Equal(publicationDate.ToDateTime(TimeOnly.MinValue), article.Date));
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData(2, 59, 10)]
    [InlineData(3, 0, 11)]
    public void ServerEditionDateFollowsGmtMinusThreeRatherThanMachineTimeZone(int hour, int minute, int day)
    {
        using var fixture = new NewsFixture(new DateOnly(2027, 11, 10),
            (_, _) => throw new InvalidOperationException("Reading the date must not request news."),
            utcNow: new DateTimeOffset(2027, 11, 11, hour, minute, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2027, 11, day), fixture.Service.Today);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingAnUncachedDayNeverStartsGeneration(bool? allowGeneration)
    {
        var date = new DateOnly(2027, 7, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))));

        var result = allowGeneration.HasValue
            ? await fixture.Service.GetNewsAsync(date, allowGeneration.Value)
            : await fixture.Service.GetNewsAsync(date);

        Assert.Equal(NewsStatus.Empty, result.Status);
        Assert.Empty(result.Articles);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task ReadReportsTheLastBackgroundFailureWithoutStartingAnotherSearch()
    {
        var date = new DateOnly(2027, 8, 10);
        using var fixture = new NewsFixture(date,
            (_, _) => Task.FromResult(Error(429, "rate_limit_exceeded", "rate_limit_error")));

        var generation = await fixture.Service.EnsureNewsAsync(date);
        var read = await fixture.Service.GetNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, generation.Status);
        Assert.Equal(NewsStatus.Unavailable, read.Status);
        Assert.Equal(generation.Message, read.Message);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task EnsurePropagatesHostCancellationWithoutRecordingATimeoutOrSavingNews()
    {
        var date = new DateOnly(2027, 9, 10);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hostStopping = new CancellationTokenSource();
        using var fixture = new NewsFixture(date, async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The host should have canceled generation.");
        });

        var generation = fixture.Service.EnsureNewsAsync(date, hostStopping.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hostStopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        var read = await fixture.Service.GetNewsAsync(date);
        Assert.Equal(NewsStatus.Empty, read.Status);
        Assert.False(fixture.Cache.TryGetValue(CacheKeys.BitcoinNewsFailure(date), out _));
        Assert.False(fixture.Cache.TryGetValue(CacheKeys.BitcoinNewsProviderFailure, out _));
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ThumbnailTimeoutDoesNotCancelPersistenceOfSuccessfullyGeneratedNews()
    {
        var date = new DateOnly(2027, 12, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            generationTimeout: TimeSpan.FromMilliseconds(500), thumbnailResolver: new TimeoutThumbnails());

        var result = await fixture.Service.EnsureNewsAsync(date);

        Assert.Equal(NewsStatus.Ok, result.Status);
        Assert.Equal(9, result.Articles.Count);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.False(fixture.Store.SaveTokenWasCanceled);
        Assert.Equal(9, fixture.Store.Days[date].Count);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task PartialDiscoveriesFromBothSearchesAreCombinedIntoOneCompleteEdition()
    {
        var date = new DateOnly(2028, 1, 10);
        var stories = BalancedStories(date);
        var callCount = 0;
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(StoriesResponse(
            ++callCount == 1 ? stories.Take(5).ToList() : stories.Skip(5).ToList()))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        Assert.Equal(stories.Select(story => story.ArticleUrl).Order(StringComparer.Ordinal),
            articles.Select(article => article.ArticleLinkUrl).Order(StringComparer.Ordinal));
        Assert.Equal(2, fixture.Requests.Count);
        using var repair = JsonDocument.Parse(fixture.Requests[1]);
        var repairText = repair.RootElement.GetProperty("input").GetRawText();
        Assert.Contains("Africa", repairText);
        Assert.Contains("Asia", repairText);
        Assert.Contains("Oceania", repairText);
        Assert.Contains("Bitcoin adoption expands", repairText);
        using var initial = JsonDocument.Parse(fixture.Requests[0]);
        Assert.Equal(initial.RootElement.GetProperty("metadata").GetProperty("news_job_id").GetString(),
            repair.RootElement.GetProperty("metadata").GetProperty("news_job_id").GetString());
        Assert.Equal("2", repair.RootElement.GetProperty("metadata").GetProperty("news_attempt").GetString());
        Assert.Equal(8, repair.RootElement.GetProperty("max_tool_calls").GetInt32());
    }

    [Fact]
    public async Task InvalidDiscoveredCandidateDoesNotDiscardTheOtherVerifiedStories()
    {
        var date = new DateOnly(2028, 2, 10);
        var stories = BalancedStories(date);
        var initial = stories.Take(5).ToList();
        initial.Add(Story(date, "Unapproved publisher story", "Unknown", "Australia", "unknown.example", "Oceania", "AU"));
        var callCount = 0;
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(StoriesResponse(
            ++callCount == 1 ? initial : stories.Skip(5).ToList()))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        Assert.Equal(stories.Select(story => story.ArticleUrl).Order(StringComparer.Ordinal),
            articles.Select(article => article.ArticleLinkUrl).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(articles, article => article.ArticleLinkUrl.Contains("unknown.example"));
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task OversuppliedPoolProducesNineBalancedStoriesWithoutAnotherSearch()
    {
        var date = new DateOnly(2028, 3, 10);
        var stories = BalancedStories(date);
        stories.InsertRange(0,
        [
            Story(date, "Mexican Bitcoin payments", "CoinDesk", "Mexico", "coindesk.com", "North America", "MX"),
            Story(date, "Another American Bitcoin story", "Decrypt", "United States", "decrypt.co", "North America", "US"),
            Story(date, "Another Canadian Bitcoin story", "Cointelegraph", "Canada", "cointelegraph.com", "North America", "CA")
        ]);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(StoriesResponse(stories))));

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        var selected = articles.Select(article => stories.Single(story => story.ArticleUrl == article.ArticleLinkUrl)).ToList();
        Assert.Equal(6, selected.Select(story => story.Continent).Distinct().Count());
        Assert.Contains(selected, story => story.CountryCode == "BR");
        Assert.InRange(selected.Count(story => story.Continent == "North America"), 1, 2);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"news\":[]}")]
    [InlineData("{\"articles\":\"unexpected\"}")]
    [InlineData("[{\"articles\":[] }]")]
    public async Task MalformedCandidatePayloadIsNotTreatedAsAnEmptyDiscovery(string json)
    {
        var date = new DateOnly(2028, 4, 10);
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(TextResponse(json))));

        var exception = await Assert.ThrowsAsync<NewsUnavailableException>(
            () => fixture.Client.GetBitcoinNewsAsync(date));

        Assert.Equal(NewsFailureReason.Upstream, exception.Reason);
        Assert.Contains("unreadable", exception.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task LegacyNullDiscoveryLogsItsCoverageExplanationBeforeRepair()
    {
        var date = new DateOnly(2028, 5, 10);
        var logger = new CapturingNewsLogger();
        var calls = 0;
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(++calls == 1
            ? TextResponse("{\"articles\":null,\"missingCoverage\":\"No verified Oceania story was found.\"}")
            : SuccessResponse(date))), logger: logger);

        var articles = await fixture.Client.GetBitcoinNewsAsync(date);

        Assert.Equal(9, articles.Count);
        Assert.Contains(logger.Lines, line => line.Contains("articles:null") && line.Contains("No verified Oceania story was found."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateJobsWithSeparateCachesReuseTheEditionSavedByTheFirstJob(bool scheduled)
    {
        var date = new DateOnly(2029, scheduled ? 2 : 1, 10);
        var clock = new MutableTimeProvider(date);
        var guard = new StubGenerationStore();
        var store = new StubStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new NewsFixture(date, async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return JsonResponse(SuccessResponse(date));
        }, store: store, timeProvider: clock, generationStore: guard);
        using var second = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            store: store, timeProvider: clock, generationStore: guard);

        var firstJob = scheduled ? first.Service.RefreshScheduledNewsAsync(date) : first.Service.EnsureNewsAsync(date);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondJob = scheduled ? second.Service.RefreshScheduledNewsAsync(date) : second.Service.EnsureNewsAsync(date);
        release.SetResult();
        var results = await Task.WhenAll(firstJob, secondJob);

        Assert.All(results, result =>
        {
            Assert.Equal(NewsStatus.Ok, result.Status);
            Assert.Equal(9, result.Articles.Count);
        });
        Assert.Single(first.Requests);
        Assert.Empty(second.Requests);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, guard.RecordedAttempts);
        Assert.False(guard.LeaseHeld);
    }

    [Fact]
    public async Task PersistentFailureCooldownSurvivesAServiceWithANewMemoryCache()
    {
        var date = new DateOnly(2029, 3, 10);
        var clock = new MutableTimeProvider(date);
        var guard = new StubGenerationStore();
        using (var failed = new NewsFixture(date,
            (_, _) => Task.FromResult(Error(429, "rate_limit_exceeded", "rate_limit_error")),
            timeProvider: clock, generationStore: guard))
        {
            Assert.Equal(NewsStatus.Unavailable, (await failed.Service.EnsureNewsAsync(date)).Status);
            Assert.Single(failed.Requests);
        }
        using var restarted = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            timeProvider: clock, generationStore: guard);

        var result = await restarted.Service.EnsureNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Empty(restarted.Requests);
        Assert.Equal(1, guard.RecordedAttempts);
    }

    [Fact]
    public async Task ThreeAutomaticAttemptsBlockAFourthEvenAfterCooldownAndCacheReset()
    {
        var date = new DateOnly(2029, 4, 10);
        var clock = new MutableTimeProvider(date);
        var guard = new StubGenerationStore();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var fixture = new NewsFixture(date,
                (_, _) => Task.FromResult(Error(429, "rate_limit_exceeded", "rate_limit_error")),
                timeProvider: clock, generationStore: guard);
            Assert.Equal(NewsStatus.Unavailable, (await fixture.Service.EnsureNewsAsync(date)).Status);
            Assert.Single(fixture.Requests);
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        using var fourth = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            timeProvider: clock, generationStore: guard);

        var blocked = await fourth.Service.EnsureNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, blocked.Status);
        Assert.Empty(fourth.Requests);
        Assert.Equal(3, guard.RecordedAttempts);
    }

    [Fact]
    public async Task AnUnavailableDatabaseLeasePreventsAnOpenAiRequest()
    {
        var date = new DateOnly(2029, 5, 10);
        var guard = new StubGenerationStore { LeaseAvailable = false };
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            generationStore: guard);

        var result = await fixture.Service.EnsureNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, guard.RecordedAttempts);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("state")]
    [InlineData("attempt")]
    public async Task GenerationGuardDatabaseErrorsFailBeforeAnyPaidRequest(string failingOperation)
    {
        var date = new DateOnly(2029, 6, 10);
        var guard = new StubGenerationStore { FailAt = failingOperation };
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            generationStore: guard);

        var result = await fixture.Service.EnsureNewsAsync(date);

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, guard.RecordedAttempts);
        Assert.False(guard.LeaseHeld);
    }

    [Theory]
    [InlineData(10, 59, 1)]
    [InlineData(11, 0, 0)]
    [InlineData(11, 1, 0)]
    public async Task ScheduledRefreshUsesTheSavedEightAmGmtMinusThreeTimestamp(int hour, int minute, int expectedRequests)
    {
        var date = new DateOnly(2029, 7, 10);
        var store = new StubStore();
        store.Days[date] = Enumerable.Range(1, 9).Select(index => new NewsArticleModel { Title = $"Saved story {index}" }).ToList();
        store.UpdatedAt[date] = new DateTimeOffset(2029, 7, 10, hour, minute, 0, TimeSpan.Zero);
        var guard = new StubGenerationStore();
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            store: store, generationStore: guard);

        var result = await fixture.Service.RefreshScheduledNewsAsync(date);
        var duplicate = await fixture.Service.RefreshScheduledNewsAsync(date);

        Assert.Equal(NewsStatus.Ok, result.Status);
        Assert.Equal(NewsStatus.Ok, duplicate.Status);
        Assert.Equal(9, result.Articles.Count);
        Assert.Equal(expectedRequests, fixture.Requests.Count);
        Assert.Equal(expectedRequests, store.SaveCount);
        Assert.Equal(expectedRequests, guard.RecordedAttempts);
    }

    [Fact]
    public async Task FailedPersistenceRetriesTheCachedEditionWithoutAnotherOpenAiCall()
    {
        var date = new DateOnly(2029, 8, 10);
        var store = new StubStore { SaveFailuresRemaining = 1 };
        var guard = new StubGenerationStore();
        using var fixture = new NewsFixture(date, (_, _) => Task.FromResult(JsonResponse(SuccessResponse(date))),
            store: store, generationStore: guard);

        var generated = await fixture.Service.EnsureNewsAsync(date);
        Assert.Equal(NewsStatus.Ok, generated.Status);
        Assert.Equal(9, generated.Articles.Count);
        Assert.Empty(store.Days);
        Assert.Equal(1, store.SaveAttemptCount);
        var retried = await fixture.Service.EnsureNewsAsync(date);

        Assert.Equal(NewsStatus.Ok, retried.Status);
        Assert.Equal(9, retried.Articles.Count);
        Assert.Single(fixture.Requests);
        Assert.Equal(2, store.SaveAttemptCount);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(9, store.Days[date].Count);
        Assert.Equal(1, guard.RecordedAttempts);
    }

    [Fact]
    public async Task AnotherReplicasRetryStatusWaitsForQueuedWorkRatherThanReturningAnOldFailure()
    {
        var date = new DateOnly(2029, 9, 10);
        var clock = new MutableTimeProvider(date);
        var guard = new StubGenerationStore();
        using (var prior = new NewsFixture(date, (_, _) => Task.FromResult(Error(429, "rate_limit_exceeded", "rate_limit_error")),
            timeProvider: clock, generationStore: guard))
            await prior.Service.EnsureNewsAsync(date);
        clock.Advance(TimeSpan.FromMinutes(2));
        var operationId = Guid.CreateVersion7(clock.GetUtcNow());
        using var other = new NewsFixture(date, (_, _) => throw new InvalidOperationException("Polling must not search."),
            timeProvider: clock, generationStore: guard);

        var status = await other.Service.GetRetryStatusAsync(operationId);

        Assert.NotNull(status);
        Assert.False(status.IsCompleted);
        Assert.Null(status.Result);
        Assert.Null(await other.Service.GetRetryStatusAsync(Guid.NewGuid()));
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task CrossReplicaPollingReportsAnOlderRunningJobThatFinishesAfterTheRetryWasRequested()
    {
        var date = new DateOnly(2029, 10, 10);
        var clock = new MutableTimeProvider(date);
        var guard = new StubGenerationStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var generating = new NewsFixture(date, async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Error(429, "rate_limit_exceeded", "rate_limit_error");
        }, timeProvider: clock, generationStore: guard);
        var job = generating.Service.EnsureNewsAsync(date);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(2));
        var operationId = Guid.CreateVersion7(clock.GetUtcNow());
        using var polling = new NewsFixture(date, (_, _) => throw new InvalidOperationException("Polling must not search."),
            timeProvider: clock, generationStore: guard);
        Assert.False((await polling.Service.GetRetryStatusAsync(operationId))!.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        release.TrySetResult();
        await job;

        var completed = await polling.Service.GetRetryStatusAsync(operationId);

        Assert.True(completed!.IsCompleted);
        Assert.Equal(NewsStatus.Unavailable, completed.Result!.Status);
        Assert.Empty(polling.Requests);
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

    private static object StoriesResponse(IReadOnlyList<NewsStory>? stories, object[]? annotations = null) => TextResponse(
        JsonSerializer.Serialize(new { articles = stories, missingCoverage = "" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), annotations);

    private static object TextResponse(string text, object[]? annotations = null) => Response(
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
            text,
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
                stories[4] = stories[4] with { PublishedDate = date.AddDays(-3).ToString("yyyy-MM-dd") };
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
            TimeSpan? generationTimeout = null, OpenAiOptions? settings = null, DateTimeOffset? utcNow = null,
            IArticleThumbnailHelper? thumbnailResolver = null,
            Microsoft.Extensions.Logging.ILogger<OpenAiNewsClient>? logger = null,
            StubStore? store = null, TimeProvider? timeProvider = null, INewsGenerationStore? generationStore = null)
        {
            Today = today;
            var options = Microsoft.Extensions.Options.Options.Create(settings ?? new OpenAiOptions
            {
                Model = "gpt-5-mini",
                GenerationTimeout = generationTimeout ?? TimeSpan.FromSeconds(10),
                FailureCooldown = TimeSpan.FromMinutes(1)
            });
            var clock = timeProvider ?? new FixedTimeProvider(today, utcNow);
            Store = store ?? new StubStore();
            Store.UtcNow = clock.GetUtcNow;
            if (generationStore is StubGenerationStore guard) guard.UtcNow = clock.GetUtcNow;
            _httpClient = new HttpClient(new StubHandler(Requests, respond));
            var sdk = new OpenAIClient(new ApiKeyCredential("test-key-never-sent-to-a-network"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://openai.invalid/v1"),
                Transport = new HttpClientPipelineTransport(_httpClient),
                RetryPolicy = new ClientRetryPolicy(0)
            });
            Client = new OpenAiNewsClient(options, clock, logger ?? NullLogger<OpenAiNewsClient>.Instance, sdk);
            Service = new BitcoinNewsService(Client, Store, thumbnailResolver ?? new NoThumbnails(), Cache, clock,
                options, NullLogger<BitcoinNewsService>.Instance, generationStore: generationStore);
        }

        public DateOnly Today { get; }
        public List<string> Requests { get; } = [];
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public StubStore Store { get; }
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

    private sealed class FixedTimeProvider(DateOnly today, DateTimeOffset? utcNow = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow ?? new(today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MutableTimeProvider(DateOnly date) : TimeProvider
    {
        private DateTimeOffset _now = new(date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class CapturingNewsLogger : Microsoft.Extensions.Logging.ILogger<OpenAiNewsClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private sealed class NoThumbnails : IArticleThumbnailHelper
    {
        public Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TimeoutThumbnails : IArticleThumbnailHelper
    {
        public Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class StubGenerationStore : INewsGenerationStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<DateOnly, NewsGenerationState> _states = [];
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
        public bool LeaseAvailable { get; set; } = true;
        public bool LeaseHeld { get; private set; }
        public string? FailAt { get; set; }
        public int RecordedAttempts { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireLeaseAsync(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FailIfRequested("lease");
            lock (_sync)
            {
                if (!LeaseAvailable || LeaseHeld) return Task.FromResult<IAsyncDisposable?>(null);
                LeaseHeld = true;
                return Task.FromResult<IAsyncDisposable?>(new Lease(this));
            }
        }

        public Task<NewsGenerationState> GetStateAsync(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FailIfRequested("state");
            lock (_sync) return Task.FromResult(State(date));
        }

        public Task RecordAttemptAsync(DateOnly date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FailIfRequested("attempt");
            lock (_sync)
            {
                if (!LeaseHeld) throw new InvalidOperationException("An attempt must hold the generation lease.");
                var state = State(date);
                _states[date] = new NewsGenerationState(state.AttemptCount + 1, UtcNow(), true, null);
                RecordedAttempts++;
            }
            return Task.CompletedTask;
        }

        public Task RecordOutcomeAsync(DateOnly date, string? failure, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!LeaseHeld) throw new InvalidOperationException("An outcome must hold the generation lease.");
                _states[date] = State(date) with { IsRunning = false, LastFailure = failure, LastCompletedUtc = UtcNow() };
            }
            return Task.CompletedTask;
        }

        private NewsGenerationState State(DateOnly date) =>
            _states.GetValueOrDefault(date) ?? new NewsGenerationState(0, null, false, null);

        private void FailIfRequested(string operation)
        {
            if (FailAt == operation) throw new InvalidOperationException($"Simulated {operation} database error.");
        }

        private sealed class Lease(StubGenerationStore owner) : IAsyncDisposable
        {
            private StubGenerationStore? _owner = owner;
            public ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    lock (owner._sync) owner.LeaseHeld = false;
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubStore : INewsStore
    {
        public Dictionary<DateOnly, List<NewsArticleModel>> Days { get; } = [];
        public Dictionary<DateOnly, DateTimeOffset> UpdatedAt { get; } = [];
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
        public int SaveCount { get; private set; }
        public int SaveAttemptCount { get; private set; }
        public int SaveFailuresRemaining { get; set; }
        public bool SaveTokenWasCanceled { get; private set; }

        public Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
            => Task.FromResult(Days.GetValueOrDefault(date));

        public Task<DateTimeOffset?> GetUpdatedAtUtcAsync(DateOnly date, CancellationToken cancellationToken = default)
            => Task.FromResult(UpdatedAt.TryGetValue(date, out var updatedAt) ? (DateTimeOffset?)updatedAt : null);

        public Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null,
            CancellationToken cancellationToken = default)
        {
            SaveTokenWasCanceled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            SaveAttemptCount++;
            if (SaveFailuresRemaining > 0)
            {
                SaveFailuresRemaining--;
                throw new InvalidOperationException("Simulated database save failure.");
            }
            SaveCount++;
            Days[date] = [.. articles];
            UpdatedAt[date] = UtcNow();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DateOnly>>([.. Days.Keys]);
    }
}
