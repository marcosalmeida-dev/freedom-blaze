using FreedomBlaze.Client.Models;
using FreedomBlaze.Services;
using FreedomBlaze.Options;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNewsRetryQueueTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConcurrentRequestsShareOnePendingOperationAndOneWorkerSignal()
    {
        var queue = new BitcoinNewsRetryQueue(new ManualClock());

        var operations = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(queue.Start)));

        Assert.Single(operations.Select(operation => operation.Id).Distinct());
        Assert.All(operations, operation =>
        {
            Assert.False(operation.IsCompleted);
            Assert.Null(operation.Result);
        });
        Assert.True(queue.HasPending);
        await WaitForWorkerSignalAsync(queue);

        // Requests arriving while the worker is already generating still join that operation.
        Assert.Equal(operations[0].Id, queue.Start().Id);
        await AssertNoWorkerSignalAsync(queue);
        queue.Complete(NewsResult.NoNews);
    }

    [Fact]
    public async Task CompletionReachesWaitersAndCanBeRetrievedByOperationId()
    {
        var queue = new BitcoinNewsRetryQueue(new ManualClock());
        var operation = queue.Start();
        var waiter = queue.RequestAsync(CancellationToken.None);
        var result = NewsResult.Ok([new NewsArticleModel { Title = "New Bitcoin edition" }]);
        await WaitForWorkerSignalAsync(queue);

        queue.Complete(result);

        Assert.Same(result, await waiter.WaitAsync(TestTimeout));
        var status = Assert.IsType<NewsRetryOperation>(queue.GetStatus(operation.Id));
        Assert.True(status.IsCompleted);
        Assert.Same(result, status.Result);
        Assert.False(queue.HasPending);
        Assert.Null(queue.GetStatus(Guid.NewGuid()));
        var recent = queue.Start();
        Assert.True(recent.IsCompleted);
        Assert.Same(result, recent.Result);
        await AssertNoWorkerSignalAsync(queue);
    }

    [Fact]
    public async Task CancelingNavigationOnlyCancelsThatReaderAndPreservesTheSharedSearch()
    {
        var queue = new BitcoinNewsRetryQueue(new ManualClock());
        using var navigation = new CancellationTokenSource();
        var canceledReader = queue.RequestAsync(navigation.Token);
        var otherReader = queue.RequestAsync(CancellationToken.None);
        var operation = queue.Start();
        await WaitForWorkerSignalAsync(queue);

        navigation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledReader.WaitAsync(TestTimeout));
        Assert.True(queue.HasPending);
        Assert.False(otherReader.IsCompleted);
        var result = NewsResult.Unavailable("No verified edition was available.");
        queue.Complete(result);
        Assert.Same(result, await otherReader.WaitAsync(TestTimeout));
        Assert.Same(result, queue.GetStatus(operation.Id)!.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecentCompletionSuppressesRetriesWhetherItWasAutomaticOrReaderRequested(bool readerRequested)
    {
        var queue = new BitcoinNewsRetryQueue(new ManualClock());
        if (readerRequested)
        {
            queue.Start();
            await WaitForWorkerSignalAsync(queue);
        }
        queue.Complete(NewsResult.Unavailable("The provider could not verify the edition."));

        var operation = queue.Start();
        var result = await queue.RequestAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.True(operation.IsCompleted);
        Assert.Equal(NewsStatus.Unavailable, operation.Result!.Status);
        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Contains("The provider could not verify the edition.", result.Message);
        Assert.False(queue.HasPending);
        await AssertNoWorkerSignalAsync(queue);
    }

    [Fact]
    public async Task FailedRetryBecomesEligibleOnlyWhenTheFailureCooldownHasElapsed()
    {
        var clock = new ManualClock();
        var queue = new BitcoinNewsRetryQueue(clock);
        var first = queue.Start();
        await WaitForWorkerSignalAsync(queue);
        queue.Complete(NewsResult.Unavailable("Previous attempt failed."));

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.True(queue.Start().IsCompleted);
        Assert.False(queue.HasPending);
        clock.Advance(TimeSpan.FromSeconds(1));
        var next = queue.Start();

        Assert.False(next.IsCompleted);
        Assert.NotEqual(first.Id, next.Id);
        Assert.True(queue.HasPending);
        await WaitForWorkerSignalAsync(queue);
        queue.Complete(NewsResult.NoNews);
    }

    [Fact]
    public async Task ShutdownCancelsPendingWaitersAndClearsThePendingOperation()
    {
        var queue = new BitcoinNewsRetryQueue(new ManualClock());
        var first = queue.RequestAsync(CancellationToken.None);
        var second = queue.RequestAsync(CancellationToken.None);
        var operation = queue.Start();
        await WaitForWorkerSignalAsync(queue);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        queue.CancelPending(stopping.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestTimeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestTimeout));
        Assert.False(queue.HasPending);
        var status = Assert.IsType<NewsRetryOperation>(queue.GetStatus(operation.Id));
        Assert.True(status.IsCompleted);
        Assert.Equal(NewsStatus.Unavailable, status.Result!.Status);
    }

    [Fact]
    public async Task AlreadyCanceledReaderDoesNotCreateAnOperation()
    {
        var queue = new BitcoinNewsRetryQueue(new ManualClock());
        using var navigation = new CancellationTokenSource();
        navigation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RequestAsync(navigation.Token));

        Assert.False(queue.HasPending);
        await AssertNoWorkerSignalAsync(queue);
    }

    [Fact]
    public async Task StatusHistoryRetainsTheLatestSixteenCompletedAttempts()
    {
        var clock = new ManualClock();
        var queue = new BitcoinNewsRetryQueue(clock);
        var ids = new List<Guid>();
        for (var attempt = 0; attempt < 18; attempt++)
        {
            var operation = queue.Start();
            ids.Add(operation.Id);
            await WaitForWorkerSignalAsync(queue);
            queue.Complete(NewsResult.Unavailable($"Attempt {attempt} failed."));
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Null(queue.GetStatus(ids[0]));
        Assert.Null(queue.GetStatus(ids[1]));
        for (var attempt = 2; attempt < ids.Count; attempt++)
        {
            var status = Assert.IsType<NewsRetryOperation>(queue.GetStatus(ids[attempt]));
            Assert.True(status.IsCompleted);
            Assert.Equal($"Attempt {attempt} failed.", status.Result!.Message);
        }
    }

    [Fact]
    public async Task FailureCooldownUsesTheConfiguredIntervalWhileSuccessUsesOneMinute()
    {
        var clock = new ManualClock();
        var queue = new BitcoinNewsRetryQueue(clock, Microsoft.Extensions.Options.Options.Create(new OpenAiOptions
        {
            FailureCooldown = TimeSpan.FromMinutes(20)
        }));
        queue.Start();
        await WaitForWorkerSignalAsync(queue);
        queue.Complete(NewsResult.Unavailable("Previous attempt failed."));

        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.True(queue.Start().IsCompleted);
        await AssertNoWorkerSignalAsync(queue);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(queue.Start().IsCompleted);
        await WaitForWorkerSignalAsync(queue);
        queue.Complete(NewsResult.Ok([new NewsArticleModel { Title = "Saved edition" }]));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(queue.Start().IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(queue.Start().IsCompleted);
        await WaitForWorkerSignalAsync(queue);
        queue.Complete(NewsResult.NoNews);
    }

    private static async Task AssertNoWorkerSignalAsync(BitcoinNewsRetryQueue queue)
    {
        using var cancellation = new CancellationTokenSource();
        var next = queue.WaitForRequestAsync(cancellation.Token);
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    private static Task WaitForWorkerSignalAsync(BitcoinNewsRetryQueue queue) =>
        queue.WaitForRequestAsync(CancellationToken.None).WaitAsync(TestTimeout);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
