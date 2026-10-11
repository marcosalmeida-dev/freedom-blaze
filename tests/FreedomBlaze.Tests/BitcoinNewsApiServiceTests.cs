using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Channels;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingNewsUsesOnlyTheReadEndpointIncludingLegacyGenerationFlag(bool allowGeneration)
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        var service = new BitcoinNewsApiService(http);

        var result = await service.GetNewsAsync(new DateOnly(2026, 10, 7), allowGeneration);

        Assert.Equal(NewsStatus.Empty, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/bitcoin-news?date=2026-10-07", request.Uri.PathAndQuery);
    }

    [Fact]
    public async Task CompletedRetryReturnsGeneratedArticlesWithoutPolling()
    {
        var articles = Enumerable.Range(1, 9)
            .Select(index => new NewsArticleModel { Title = $"Bitcoin headline {index}" }).ToList();
        var handler = new RetryHandler((_, _) => Task.FromResult(RetryResponse(
            HttpStatusCode.OK, new NewsRetryOperation(Guid.NewGuid(), true, NewsResult.Ok(articles)))));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        var service = new BitcoinNewsApiService(http);

        var result = await service.RetryNewsAsync(new DateOnly(2026, 10, 7));

        Assert.Equal(NewsStatus.Ok, result.Status);
        Assert.Equal(articles.Select(article => article.Title), result.Articles.Select(article => article.Title));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/bitcoin-news/retry?date=2026-10-07", request.Uri.PathAndQuery);
    }

    [Fact]
    public async Task CompletedRetryPreservesTheProviderFailureMessage()
    {
        const string failure = "Bitcoin news is paused because the news provider quota has run out.";
        var handler = new RetryHandler((_, _) => Task.FromResult(RetryResponse(
            HttpStatusCode.OK, new NewsRetryOperation(Guid.NewGuid(), true, NewsResult.Unavailable(failure)))));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        var service = new BitcoinNewsApiService(http);

        var result = await service.RetryNewsAsync(new DateOnly(2026, 10, 7));

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Equal(failure, result.Message);
        Assert.Empty(result.Articles);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PendingRetryPollsItsOperationAndReturnsAllNineArticles()
    {
        var operationId = Guid.NewGuid();
        var articles = Enumerable.Range(1, 9)
            .Select(index => new NewsArticleModel { Title = $"Generated headline {index}" }).ToList();
        var handler = new RetryHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? RetryResponse(HttpStatusCode.Accepted, new NewsRetryOperation(operationId, false, null))
                : RetryResponse(HttpStatusCode.OK, new NewsRetryOperation(operationId, true, NewsResult.Ok(articles)))));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        var clock = new ManualPollClock();
        var service = new BitcoinNewsApiService(http, clock);

        var retry = service.RetryNewsAsync(new DateOnly(2026, 10, 7));
        var pollingDelay = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), pollingDelay.DueTime);
        Assert.False(retry.IsCompleted);
        Assert.Single(handler.Requests);
        pollingDelay.Fire();
        var result = await retry.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NewsStatus.Ok, result.Status);
        Assert.Equal(9, result.Articles.Count);
        Assert.Equal(articles.Select(article => article.Title), result.Articles.Select(article => article.Title));
        Assert.Collection(handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/bitcoin-news/retry?date=2026-10-07", request.Uri.PathAndQuery);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal($"/api/bitcoin-news/retry/{operationId:D}", request.Uri.PathAndQuery);
            });
    }

    [Fact]
    public async Task MissingRetryStatusIsUnavailableAndDoesNotQueueAnotherSearch()
    {
        var operationId = Guid.NewGuid();
        const string failure = "The news retry no longer exists. Please try again.";
        var handler = new RetryHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? RetryResponse(HttpStatusCode.Accepted, new NewsRetryOperation(operationId, false, null))
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = JsonContent.Create(new { detail = failure })
                }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        var clock = new ManualPollClock();
        var service = new BitcoinNewsApiService(http, clock);

        var retry = service.RetryNewsAsync(new DateOnly(2026, 10, 7));
        (await clock.NextTimerAsync()).Fire();
        var result = await retry.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Equal(failure, result.Message);
        Assert.Empty(result.Articles);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal($"/api/bitcoin-news/retry/{operationId:D}", handler.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task CancelingPendingRetryPropagatesCancellationWithoutStartingAnotherRequest()
    {
        var handler = new RetryHandler((_, _) => Task.FromResult(RetryResponse(
            HttpStatusCode.Accepted, new NewsRetryOperation(Guid.NewGuid(), false, null))));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualPollClock();
        var service = new BitcoinNewsApiService(http, clock);

        var retry = service.RetryNewsAsync(new DateOnly(2026, 10, 7), cancellation.Token);
        await clock.NextTimerAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancelingRetryPostPropagatesCancellationToTheHttpRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RetryHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled HTTP request must not complete successfully.");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://news.invalid/") };
        using var cancellation = new CancellationTokenSource();
        var service = new BitcoinNewsApiService(http);

        var retry = service.RetryNewsAsync(new DateOnly(2026, 10, 7), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage RetryResponse(HttpStatusCode status, NewsRetryOperation operation) =>
        new(status) { Content = JsonContent.Create(operation) };

    private static HttpClient CreateClient(HttpStatusCode status, string body) =>
        new(new ResponseHandler(status, body)) { BaseAddress = new Uri("https://news.invalid/") };

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RetryHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!));
            return respond(request, cancellationToken);
        }
    }

    private sealed class ManualPollClock : TimeProvider
    {
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        public Task<ManualTimer> NextTimerAsync() =>
            _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _disposed;
        public TimeSpan DueTime { get; } = dueTime;

        public bool Change(TimeSpan changedDueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;

        public void Fire()
        {
            if (Volatile.Read(ref _disposed) == 0) { callback(state); }
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
