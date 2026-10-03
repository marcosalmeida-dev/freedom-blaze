using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreedomBlaze.Tests;

public sealed class ExchangeRateStateTests
{
    [Fact]
    public async Task LoadingIsLazyAndConcurrentReadersShareTheInitialRequest()
    {
        var response = new TaskCompletionSource<BitcoinExchangeRateModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubRateService(token =>
        {
            started.TrySetResult();
            return response.Task.WaitAsync(token);
        });
        await using var state = CreateState(service);
        Assert.Equal(0, service.Calls);

        var first = state.EnsureRatesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = state.EnsureRatesAsync();
        var third = state.RefreshAsync();
        response.SetResult(Rate(60_000));
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, service.Calls);
        Assert.Equal(60_000, state.BitcoinExchangeRate!.BitcoinRateInUSD);
        Assert.False(state.HasRateError);
    }

    [Fact]
    public async Task CancelingOneReaderDoesNotCancelTheSharedRequest()
    {
        var response = new TaskCompletionSource<BitcoinExchangeRateModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubRateService(token => response.Task.WaitAsync(token));
        await using var state = CreateState(service);
        using var cancellation = new CancellationTokenSource();

        var canceledReader = state.EnsureRatesAsync(cancellation.Token);
        var remainingReader = state.EnsureRatesAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledReader);

        response.SetResult(Rate(62_000));
        await remainingReader;
        Assert.Equal(1, service.Calls);
        Assert.Equal(62_000, state.BitcoinExchangeRate!.BitcoinRateInUSD);
    }

    [Fact]
    public async Task RefreshPublishesCompleteStateOnceAndAwaitsEverySubscriber()
    {
        var service = new StubRateService(_ => Task.FromResult<BitcoinExchangeRateModel?>(Rate(63_000)));
        service.BitcoinExchangeStatusList.Add(new() { ExchangeName = "Healthy", IsExchangeAvailable = true });
        await using var state = CreateState(service);
        var subscriberStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubscriber = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        BitcoinExchangeRateModel? publishedRate = null;
        DateTimeOffset publishedTimestamp = default;
        List<BitcoinExchangeStatusModel>? publishedStatuses = null;
        state.OnChange += async () =>
        {
            Interlocked.Increment(ref notifications);
            publishedRate = state.BitcoinExchangeRate;
            publishedTimestamp = state.LastUpdate;
            publishedStatuses = state.ExchangeStatusList;
            subscriberStarted.TrySetResult();
            await releaseSubscriber.Task;
        };
        state.OnChange += () => throw new InvalidOperationException("Disconnected reader");
        state.OnChange += () => Task.CompletedTask;

        var load = state.EnsureRatesAsync();
        try
        {
            await subscriberStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(load.IsCompleted);
        }
        finally
        {
            releaseSubscriber.TrySetResult();
        }
        await load;

        Assert.Equal(1, notifications);
        Assert.Equal(63_000, publishedRate!.BitcoinRateInUSD);
        Assert.NotEqual(default, publishedTimestamp);
        Assert.True(Assert.Single(publishedStatuses!).IsExchangeAvailable);
    }

    [Fact]
    public async Task FailedRefreshPreservesLastUsableRateAndTimestamp()
    {
        var service = new StubRateService(_ => Task.FromResult<BitcoinExchangeRateModel?>(Rate(64_000)));
        await using var state = CreateState(service);
        await state.EnsureRatesAsync();
        var timestamp = state.LastUpdate;

        service.Fetch = _ => Task.FromResult<BitcoinExchangeRateModel?>(null);
        service.BitcoinExchangeStatusList.Add(new() { ExchangeName = "Unavailable" });
        await state.RefreshAsync();

        Assert.True(state.HasRateError);
        Assert.Equal(64_000, state.BitcoinExchangeRate!.BitcoinRateInUSD);
        Assert.Equal(timestamp, state.LastUpdate);
        Assert.False(Assert.Single(state.ExchangeStatusList).IsExchangeAvailable);
    }

    [Fact]
    public async Task ReadingTheSameCachedSnapshotDoesNotAdvanceItsTimestamp()
    {
        var rate = Rate(64_000);
        var service = new StubRateService(_ => Task.FromResult<BitcoinExchangeRateModel?>(rate));
        var clock = new TestTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero) };
        await using var state = new AppState(service, NullLogger<AppState>.Instance, clock);
        await state.EnsureRatesAsync();
        var timestamp = state.LastUpdate;

        clock.UtcNow += TimeSpan.FromSeconds(30);
        await state.RefreshAsync();
        Assert.Equal(timestamp, state.LastUpdate);

        rate = Rate(65_000);
        await state.RefreshAsync();
        Assert.Equal(clock.UtcNow, state.LastUpdate);
    }

    [Fact]
    public async Task DisposalCancelsAnInFlightRefreshWithoutPublishingAnError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubRateService(async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        });
        var state = CreateState(service);
        var notifications = 0;
        state.OnChange += () => { notifications++; return Task.CompletedTask; };
        var load = state.EnsureRatesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await state.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await load;

        Assert.Equal(0, notifications);
        Assert.False(state.HasRateError);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => state.EnsureRatesAsync());
    }

    [Fact]
    public async Task ConcurrentServiceReadsFetchProvidersOnceAndInParallel()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var exchangeResponse = new TaskCompletionSource<BitcoinExchangeRateModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currencyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new StubExchange("Exchange", token => exchangeResponse.Task.WaitAsync(token));
        var currencies = new StubCurrencies(_ =>
        {
            currencyStarted.TrySetResult();
            return Task.FromResult(Currencies());
        });
        var service = new ExchangeRateService(cache, currencies, [exchange]);

        var reads = Enumerable.Range(0, 20).Select(_ => service.GetExchangeRateAsync(CancellationToken.None)).ToArray();
        try
        {
            // Fiat starts before the Bitcoin provider has completed.
            await currencyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            exchangeResponse.TrySetResult(Rate(65_000));
        }
        var results = await Task.WhenAll(reads);

        Assert.Equal(1, exchange.Calls);
        Assert.Equal(1, currencies.Calls);
        Assert.All(results, result => Assert.Equal(65_000, result!.BitcoinRateInUSD));
    }

    [Fact]
    public async Task CachedSnapshotsRestoreSourceHealthAndIgnoreInvalidRates()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var healthy = new StubExchange("Healthy", _ => Task.FromResult(Rate(66_000)));
        var invalid = new StubExchange("Invalid", _ => Task.FromResult(Rate(-1)));
        var failed = new StubExchange("Failed", _ => throw new HttpRequestException("Unavailable"));
        var currencies = new StubCurrencies(_ => Task.FromResult(Currencies()));
        var service = new ExchangeRateService(cache, currencies, [healthy, invalid, failed]);
        await service.GetExchangeRateAsync(CancellationToken.None);
        var anotherReader = new ExchangeRateService(cache, currencies, [healthy, invalid, failed]);

        var result = await anotherReader.GetExchangeRateAsync(CancellationToken.None);

        Assert.Equal(66_000, result!.BitcoinRateInUSD);
        Assert.Equal(3, anotherReader.BitcoinExchangeStatusList.Count);
        Assert.Equal("Healthy", Assert.Single(anotherReader.BitcoinExchangeStatusList, status => status.IsExchangeAvailable).ExchangeName);
        Assert.Equal(1, healthy.Calls);
        Assert.Equal(1, currencies.Calls);
    }

    [Fact]
    public async Task ProviderCancellationIsPropagatedAndIsNotCachedAsFailure()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new StubExchange("Canceled", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Rate(1);
        });
        var currencies = new StubCurrencies(_ => Task.FromResult(Currencies()));
        var service = new ExchangeRateService(cache, currencies, [exchange]);
        var canceled = service.GetExchangeRateAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        exchange.Fetch = _ => Task.FromResult(Rate(67_000));
        var recovered = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.Equal(67_000, recovered!.BitcoinRateInUSD);
        Assert.Equal(2, exchange.Calls);
    }

    [Fact]
    public async Task FailedSnapshotsBrieflyPreventRepeatedProviderRequests()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var exchange = new StubExchange("Unavailable", _ => throw new HttpRequestException());
        var currencies = new StubCurrencies(_ => Task.FromResult(Currencies()));
        var service = new ExchangeRateService(cache, currencies, [exchange]);

        Assert.Null(await service.GetExchangeRateAsync(CancellationToken.None));
        Assert.Null(await service.GetExchangeRateAsync(CancellationToken.None));

        Assert.Equal(1, exchange.Calls);
        Assert.False(Assert.Single(service.BitcoinExchangeStatusList).IsExchangeAvailable);
    }

    private static AppState CreateState(IExchangeRateService service) =>
        new(service, NullLogger<AppState>.Instance, TimeProvider.System);

    private static BitcoinExchangeRateModel Rate(decimal value) => new()
    {
        BitcoinRateInUSD = value,
        CurrencyExchangeRate = Currencies()
    };

    private static CurrencyExchangeRateModel Currencies() => new()
    {
        Rates = [new() { Currency = "USD", Rate = 1 }]
    };

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StubRateService(Func<CancellationToken, Task<BitcoinExchangeRateModel?>> fetch) : IExchangeRateService
    {
        public Func<CancellationToken, Task<BitcoinExchangeRateModel?>> Fetch { get; set; } = fetch;
        public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; } = [];
        public int Calls;
        public Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Fetch(cancellationToken);
        }
    }

    private sealed class StubExchange(string name, Func<CancellationToken, Task<BitcoinExchangeRateModel>> fetch) : IBitcoinExchangeRateClient
    {
        public string ExchangeName => name;
        public Func<CancellationToken, Task<BitcoinExchangeRateModel>> Fetch { get; set; } = fetch;
        public int Calls;
        public Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Fetch(cancellationToken);
        }
    }

    private sealed class StubCurrencies(Func<CancellationToken, Task<CurrencyExchangeRateModel>> fetch) : ICurrencyExchangeRateClient
    {
        public int Calls;
        public Task<CurrencyExchangeRateModel> GetCurrencyRateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return fetch(cancellationToken);
        }
    }
}
