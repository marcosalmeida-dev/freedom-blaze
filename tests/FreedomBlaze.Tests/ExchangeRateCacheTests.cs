using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreedomBlaze.Tests;

public sealed class ExchangeRateCacheTests
{
    [Fact]
    public async Task BitcoinSnapshotExpiresAfterOneMinuteDespiteRepeatedReads()
    {
        var clock = new TestClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var exchange = new StubExchange();
        var currencies = new StubCurrencies();
        var service = new ExchangeRateService(cache, currencies, [exchange], clock);

        var initial = await service.GetExchangeRateAsync(CancellationToken.None);
        Assert.Equal(60_001m, initial!.BitcoinRateInUSD);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Same(initial, await service.GetExchangeRateAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(initial, await service.GetExchangeRateAsync(CancellationToken.None));
        Assert.Equal(1, exchange.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.NotSame(initial, refreshed);
        Assert.Equal(60_002m, refreshed!.BitcoinRateInUSD);
        Assert.Equal(2, exchange.Calls);
    }

    [Fact]
    public async Task CurrencyRatesExpireAfterOneHourDespiteRepeatedReads()
    {
        var clock = new TestClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var currencies = new StubCurrencies();
        var settings = new CurrencyExchangeOptions();
        using var providers = new ServiceCollection()
            .AddKeyedSingleton<ICurrencyExchangeRateClient>(settings.Provider, currencies)
            .BuildServiceProvider();
        var service = new CurrencyExchangeRateService(providers, cache,
            Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<CurrencyExchangeRateService>.Instance, clock);

        Assert.Equal(TimeSpan.FromHours(1), settings.CacheDuration);
        var initial = await service.GetCurrencyRateAsync(CancellationToken.None);
        Assert.Equal(5.01m, initial["BRL"].Rate);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Same(initial, await service.GetCurrencyRateAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        Assert.Same(initial, await service.GetCurrencyRateAsync(CancellationToken.None));
        Assert.Equal(1, currencies.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await service.GetCurrencyRateAsync(CancellationToken.None);

        Assert.NotSame(initial, refreshed);
        Assert.Equal(5.02m, refreshed["BRL"].Rate);
        Assert.Equal(2, currencies.Calls);
    }

    [Fact]
    public async Task MinuteBitcoinRefreshesReuseCurrenciesUntilTheHourExpires()
    {
        var clock = new TestClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var currencies = new StubCurrencies();
        var settings = new CurrencyExchangeOptions();
        using var providers = new ServiceCollection()
            .AddKeyedSingleton<ICurrencyExchangeRateClient>(settings.Provider, currencies)
            .BuildServiceProvider();
        var currencyService = new CurrencyExchangeRateService(providers, cache,
            Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<CurrencyExchangeRateService>.Instance, clock);
        var exchange = new StubExchange();
        var service = new ExchangeRateService(cache, currencyService, [exchange], clock);
        var initial = await service.GetExchangeRateAsync(CancellationToken.None);

        for (var minute = 1; minute < 60; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var refreshed = await service.GetExchangeRateAsync(CancellationToken.None);
            Assert.Equal(60_001m + minute, refreshed!.BitcoinRateInUSD);
            Assert.Same(initial!.CurrencyExchangeRate, refreshed.CurrencyExchangeRate);
            Assert.Equal(1, currencies.Calls);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        var hourRefresh = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.NotSame(initial!.CurrencyExchangeRate, hourRefresh!.CurrencyExchangeRate);
        Assert.Equal(61, exchange.Calls);
        Assert.Equal(2, currencies.Calls);
        Assert.Equal(5.02m, hourRefresh.CurrencyExchangeRate!["BRL"].Rate);
    }

    [Fact]
    public async Task BitcoinSnapshotCannotExtendCurrencyRatesPastTheirHourlyExpiry()
    {
        var clock = new TestClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var currencies = new StubCurrencies();
        var settings = new CurrencyExchangeOptions();
        using var providers = new ServiceCollection()
            .AddKeyedSingleton<ICurrencyExchangeRateClient>(settings.Provider, currencies)
            .BuildServiceProvider();
        var currencyService = new CurrencyExchangeRateService(providers, cache,
            Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<CurrencyExchangeRateService>.Instance, clock);
        var exchange = new StubExchange();
        var service = new ExchangeRateService(cache, currencyService, [exchange], clock);
        var initial = await service.GetExchangeRateAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        var beforeExpiry = await service.GetExchangeRateAsync(CancellationToken.None);
        Assert.NotSame(initial, beforeExpiry);
        Assert.Same(initial!.CurrencyExchangeRate, beforeExpiry!.CurrencyExchangeRate);
        Assert.Equal(2, exchange.Calls);
        Assert.Equal(1, currencies.Calls);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Same(beforeExpiry, await service.GetExchangeRateAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        var refreshed = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.NotSame(beforeExpiry, refreshed);
        Assert.NotSame(initial.CurrencyExchangeRate, refreshed!.CurrencyExchangeRate);
        Assert.Equal(3, exchange.Calls);
        Assert.Equal(2, currencies.Calls);
        Assert.Equal(5.02m, refreshed.CurrencyExchangeRate!["BRL"].Rate);
    }

    private sealed class TestClock : TimeProvider, ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class StubExchange : IBitcoinExchangeRateClient
    {
        public string ExchangeName => "Test exchange";
        public int Calls { get; private set; }

        public Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new BitcoinExchangeRateModel { BitcoinRateInUSD = 60_000m + Calls });
        }
    }

    private sealed class StubCurrencies : ICurrencyExchangeRateClient
    {
        public int Calls { get; private set; }

        public Task<CurrencyExchangeRateModel> GetCurrencyRateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new CurrencyExchangeRateModel
            {
                Rates =
                [
                    new() { Currency = "USD", Rate = 1m },
                    new() { Currency = "BRL", Rate = 5m + Calls / 100m }
                ]
            });
        }
    }
}
