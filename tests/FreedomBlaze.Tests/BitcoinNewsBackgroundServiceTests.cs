using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Threading.Channels;
using FreedomBlaze.Client.Models;
using FreedomBlaze.Clients;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNewsBackgroundServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("2026-10-07T02:59:59Z", "2026-10-06")]
    [InlineData("2026-10-07T03:00:00Z", "2026-10-07")]
    [InlineData("2027-01-01T02:00:00Z", "2026-12-31")]
    public void EditionDateUsesFixedGmtMinusThree(string instant, string expectedDate)
    {
        Assert.Equal(DateOnly.ParseExact(expectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            BitcoinNewsSchedule.Today(Instant(instant)));
        Assert.False(BitcoinNewsSchedule.TimeZone.SupportsDaylightSavingTime);
    }

    [Theory]
    [InlineData("2026-10-07T10:59:59Z", "2026-10-07T11:00:00Z")]
    [InlineData("2026-10-07T11:00:00Z", "2026-10-08T11:00:00Z")]
    [InlineData("2026-10-07T11:00:01Z", "2026-10-08T11:00:00Z")]
    [InlineData("2026-12-31T23:00:00Z", "2027-01-01T11:00:00Z")]
    [InlineData("2026-07-07T07:00:00-03:00", "2026-07-07T11:00:00Z")]
    public void DailyRunIsNextEightAmAndDoesNotRepeatAtExactBoundary(string now, string expected)
    {
        var next = BitcoinNewsSchedule.GetNextRunUtc(Instant(now));

        Assert.Equal(Instant(expected), next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
    }

    [Fact]
    public async Task StartupDoesNotBlockHostAndWaitsUntilApplicationHasStarted()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:00:00Z"));
        using var lifetime = new TestLifetime();
        var scopes = new FailingScopeFactory();
        var logger = new RecordingLogger();
        using var worker = new BitcoinNewsBackgroundService(scopes, clock, lifetime, logger);

        await worker.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        Assert.Equal(0, scopes.Created);

        lifetime.SignalStarted();
        var timer = await clock.NextTimerAsync();

        Assert.Equal(1, scopes.Created);
        Assert.Equal(1, scopes.Disposed);
        Assert.Equal(TimeSpan.FromHours(1), timer.DueTime);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("startup load"));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData("2026-10-07T11:00:00Z", 24)]
    [InlineData("2026-10-07T12:00:00Z", 23)]
    public async Task StartupAtOrAfterEightRunsOnceAndFailuresDoNotStopFollowingDays(string start, int hoursToNextRun)
    {
        var clock = new ManualClock(Instant(start));
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        var scopes = new FailingScopeFactory();
        var logger = new RecordingLogger();
        using var worker = new BitcoinNewsBackgroundService(scopes, clock, lifetime, logger);
        await worker.StartAsync(CancellationToken.None);
        var next = await clock.NextTimerAsync();
        Assert.Equal(1, scopes.Created);
        Assert.Equal(TimeSpan.FromHours(hoursToNextRun), next.DueTime);

        for (var day = 8; day <= 9; day++)
        {
            clock.SetUtcNow(new DateTimeOffset(2026, 10, day, 11, 0, 0, TimeSpan.Zero));
            next.Fire();
            next = await clock.NextTimerAsync();
            Assert.Equal(TimeSpan.FromDays(1), next.DueTime);
        }

        Assert.Equal(3, scopes.Created);
        Assert.Equal(3, scopes.Disposed);
        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Error));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task StartupReusesSavedNewsAndOnlyTheDailyRunRequestsARefresh()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:00:00Z"));
        var store = new TestStore();
        var calls = 0;
        using var http = new HttpClient(new ControlledHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"credit_balance_exhausted","type":"insufficient_quota","message":"test-only quota error"}}""")
            });
        }));
        using var provider = Services(clock, store, Sdk(http));
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        var logger = new RecordingLogger();
        using var worker = new BitcoinNewsBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, lifetime, logger);

        await worker.StartAsync(CancellationToken.None);
        var timer = await clock.NextTimerAsync();
        Assert.Single(store.ReadDates);
        Assert.Equal(0, calls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("startup load"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);

        clock.SetUtcNow(Instant("2026-10-07T11:00:00Z"));
        timer.Fire();
        await clock.NextTimerAsync();

        // Startup serves the saved edition without OpenAI. Its metadata is absent in this stub,
        // so the scheduled refresh makes exactly one attempt through the fake HTTP transport.
        Assert.Equal(1, calls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("daily refresh"));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ReaderRetryRunsInTheWorkerAndPreservesTheNextEightAmRefresh()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:00:00Z"));
        var queue = new BitcoinNewsRetryQueue(clock);
        using var provider = Services(clock, new TestStore(hasSavedEdition: false), retryQueue: queue);
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        var logger = new RecordingLogger();
        using var worker = new BitcoinNewsBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, lifetime, logger,
            provider.GetRequiredService<BitcoinNewsRetryQueue>());

        await worker.StartAsync(CancellationToken.None);
        // Consume the original daily timer before waking the worker through its retry queue.
        // Its cancellation must not be mistaken for the replacement timer after the retry.
        var initialTimer = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromHours(1), initialTimer.DueTime);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("startup load"));

        clock.SetUtcNow(Instant("2026-10-07T10:11:00Z"));
        var operation = queue.Start();
        Assert.False(operation.IsCompleted);
        var nextTimer = await clock.NextTimerAsync();

        var completed = Assert.IsType<NewsRetryOperation>(queue.GetStatus(operation.Id));
        Assert.True(completed.IsCompleted);
        Assert.Equal(NewsStatus.Unavailable, completed.Result!.Status);
        Assert.False(queue.HasPending);
        Assert.Equal(TimeSpan.FromMinutes(49), nextTimer.DueTime);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("reader retry"));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("daily refresh"));

        // The retry consumed no scheduled run: today's original 08:00 still refreshes once.
        clock.SetUtcNow(Instant("2026-10-07T11:00:00Z"));
        nextTimer.Fire();
        var followingDay = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromDays(1), followingDay.DueTime);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("daily refresh"));
        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupAfterEightReusesFreshNewsAndCatchesUpOnlyAnOlderEdition(bool alreadyRefreshed)
    {
        var clock = new ManualClock(Instant("2026-10-07T12:00:00Z"));
        var store = new TestStore(updatedAtUtc: Instant(alreadyRefreshed
            ? "2026-10-07T11:00:00Z" : "2026-10-07T10:00:00Z"));
        var calls = 0;
        using var http = new HttpClient(new ControlledHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"credit_balance_exhausted","type":"insufficient_quota","message":"test-only quota error"}}""")
            });
        }));
        using var provider = Services(clock, store, Sdk(http));
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        var logger = new RecordingLogger();
        using var worker = new BitcoinNewsBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, lifetime, logger);

        await worker.StartAsync(CancellationToken.None);
        var next = await clock.NextTimerAsync();

        Assert.Equal(alreadyRefreshed ? 0 : 1, calls);
        Assert.Equal(TimeSpan.FromHours(23), next.DueTime);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("startup load") &&
            entry.Level == (alreadyRefreshed ? LogLevel.Information : LogLevel.Warning));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ReaderRetryReusesAnEditionSavedBeforeItsWorkerWakes()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:00:00Z"));
        var queue = new BitcoinNewsRetryQueue(clock);
        var calls = 0;
        using var http = new HttpClient(new ControlledHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("A saved edition must never trigger OpenAI.");
        }));
        using var provider = Services(clock, new TestStore(), Sdk(http), queue);
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        using var worker = new BitcoinNewsBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, lifetime,
            new RecordingLogger(), queue);
        await worker.StartAsync(CancellationToken.None);
        await clock.NextTimerAsync();

        clock.SetUtcNow(Instant("2026-10-07T10:02:00Z"));
        var operation = queue.Start();
        var next = await clock.NextTimerAsync();

        Assert.False(operation.IsCompleted);
        var completed = Assert.IsType<NewsRetryOperation>(queue.GetStatus(operation.Id));
        Assert.True(completed.IsCompleted);
        Assert.Equal(NewsStatus.Ok, completed.Result!.Status);
        Assert.Equal(9, completed.Result.Articles.Count);
        Assert.Equal(0, calls);
        Assert.Equal(TimeSpan.FromMinutes(58), next.DueTime);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task StartupFailureCrossingEightAmDoesNotTriggerAnotherPaidCall()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:59:00Z"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new ControlledHandler(async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"credit_balance_exhausted","type":"insufficient_quota","message":"test-only quota error"}}"""),
            };
        }));
        using var provider = Services(clock, new TestStore(hasSavedEdition: false), Sdk(http));
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        var logger = new RecordingLogger();
        using var worker = new BitcoinNewsBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, lifetime, logger);

        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TestTimeout);
        clock.SetUtcNow(Instant("2026-10-07T11:02:00Z"));
        finish.TrySetResult();
        var timer = await clock.NextTimerAsync();

        Assert.Equal(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(2), timer.DueTime);
        Assert.Equal(1, calls);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("daily refresh"));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task WakeRechecksClockBeforeRunningAfterBackwardAdjustment()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:00:00Z"));
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        var scopes = new FailingScopeFactory();
        using var worker = new BitcoinNewsBackgroundService(scopes, clock, lifetime, new RecordingLogger());
        await worker.StartAsync(CancellationToken.None);
        var timer = await clock.NextTimerAsync();

        clock.SetUtcNow(Instant("2026-10-07T10:30:00Z"));
        timer.Fire();
        var correctedTimer = await clock.NextTimerAsync();

        Assert.Equal(TimeSpan.FromMinutes(30), correctedTimer.DueTime);
        Assert.Equal(1, scopes.Created);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ShutdownBeforeApplicationStartedDoesNotGenerateNews()
    {
        var scopes = new FailingScopeFactory();
        using var lifetime = new TestLifetime();
        using var worker = new BitcoinNewsBackgroundService(scopes,
            new ManualClock(Instant("2026-10-07T10:00:00Z")), lifetime, new RecordingLogger());

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(0, scopes.Created);
        // BackgroundService can cancel its scheduled ExecuteAsync before the method starts.
        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task ShutdownCancelsActiveStartupWork()
    {
        var clock = new ManualClock(Instant("2026-10-07T10:00:00Z"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        using var http = new HttpClient(new ControlledHandler(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                throw;
            }

            throw new InvalidOperationException("The infinite wait should only complete through cancellation.");
        }));
        using var provider = Services(clock, new TestStore(hasSavedEdition: false), Sdk(http));
        using var lifetime = new TestLifetime();
        lifetime.SignalStarted();
        using var worker = new BitcoinNewsBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, lifetime, new RecordingLogger());

        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TestTimeout);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.True(canceled);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static ServiceProvider Services(TimeProvider clock, TestStore store, OpenAIClient? sdk = null,
        BitcoinNewsRetryQueue? retryQueue = null)
    {
        var services = new ServiceCollection();
        var options = Microsoft.Extensions.Options.Options.Create(new OpenAiOptions());
        services.AddMemoryCache();
        if (retryQueue is not null) services.AddSingleton(retryQueue);
        services.AddScoped(provider => new BitcoinNewsService(
            new OpenAiNewsClient(options, clock, NullLogger<OpenAiNewsClient>.Instance, sdk),
            store, new NoThumbnails(), provider.GetRequiredService<IMemoryCache>(), clock,
            options, NullLogger<BitcoinNewsService>.Instance, provider.GetService<BitcoinNewsRetryQueue>()));
        return services.BuildServiceProvider();
    }

    private static OpenAIClient Sdk(HttpClient http) => new(
        new ApiKeyCredential("test-key-never-sent-to-a-network"),
        new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openai.invalid/v1"),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0),
        });

    private sealed class ControlledHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(cancellationToken);
    }

    private sealed class TestStore(bool hasSavedEdition = true, DateTimeOffset? updatedAtUtc = null) : INewsStore
    {
        public ConcurrentQueue<DateOnly> ReadDates { get; } = new();
        public Task<List<NewsArticleModel>?> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
        {
            ReadDates.Enqueue(date);
            return Task.FromResult<List<NewsArticleModel>?>(hasSavedEdition
                ? Enumerable.Range(1, 9).Select(index => new NewsArticleModel
                {
                    Title = $"Saved news {index}", Date = date.ToDateTime(TimeOnly.MinValue)
                }).ToList()
                : null);
        }

        public Task SaveAsync(DateOnly date, IReadOnlyList<NewsArticleModel> articles, string? model = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DateTimeOffset?> GetUpdatedAtUtcAsync(DateOnly date, CancellationToken cancellationToken = default) =>
            Task.FromResult(updatedAtUtc);

        public Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DateOnly>>([]);
    }

    private sealed class NoThumbnails : IArticleThumbnailHelper
    {
        public Task ResolveAsync(IReadOnlyList<NewsArticleModel> articles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void SignalStarted() => _started.Cancel();
        public void StopApplication() { }
        public void Dispose() => _started.Dispose();
    }

    private sealed class FailingScopeFactory : IServiceScopeFactory
    {
        public int Created;
        public int Disposed;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Created);
            return new FailingScope(this);
        }

        private sealed class FailingScope(FailingScopeFactory owner) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => throw new InvalidOperationException("Simulated scoped service failure.");
            public void Dispose() => Interlocked.Increment(ref owner.Disposed);
        }
    }

    private sealed class RecordingLogger : ILogger<BitcoinNewsBackgroundService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();
        private DateTimeOffset _now = now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test-UTC+14", TimeSpan.FromHours(14), "test", "test");

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) { return _now; }
        }

        public void SetUtcNow(DateTimeOffset value)
        {
            lock (_gate) { _now = value; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        public Task<ManualTimer> NextTimerAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _disposed;
        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool Change(TimeSpan changedDueTime, TimeSpan period)
        {
            DueTime = changedDueTime;
            return Volatile.Read(ref _disposed) == 0;
        }

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
