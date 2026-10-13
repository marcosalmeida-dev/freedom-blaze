using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using Microsoft.Extensions.Caching.Memory;

namespace FreedomBlaze.Tests;

public sealed class ExchangeRateMedianTests
{
    [Fact]
    public async Task OneWildExchangeCannotDragTheAggregatedPrice()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ExchangeRateService(cache, new FixedCurrencies(),
            [Exchange("A", 80_000m), Exchange("B", 80_100m), Exchange("Broken", 120_000m)]);

        var rate = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.Equal(80_100m, rate!.BitcoinRateInUSD);
        Assert.Equal(3, rate.SourceCount);
    }

    [Fact]
    public async Task EvenSourceCountUsesTheMeanOfTheMiddlePair()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ExchangeRateService(cache, new FixedCurrencies(),
            [Exchange("A", 80_000m), Exchange("B", 80_400m), Exchange("C", 80_200m), Exchange("D", 79_000m)]);

        var rate = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.Equal(80_100m, rate!.BitcoinRateInUSD);
        Assert.Equal(4, rate.SourceCount);
    }

    [Fact]
    public async Task FailedAndInvalidSourcesAreNotCounted()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ExchangeRateService(cache, new FixedCurrencies(),
            [Exchange("Healthy", 80_000m), Exchange("Zero", 0m), new StubExchange("Down", () => throw new HttpRequestException())]);

        var rate = await service.GetExchangeRateAsync(CancellationToken.None);

        Assert.Equal(80_000m, rate!.BitcoinRateInUSD);
        Assert.Equal(1, rate.SourceCount);
    }

    [Theory]
    [InlineData(new[] { 7.0 }, 7.0)]
    [InlineData(new[] { 3.0, 1.0, 2.0 }, 2.0)]
    [InlineData(new[] { 4.0, 1.0, 3.0, 2.0 }, 2.5)]
    [InlineData(new[] { 1.0, 1.0, 1_000_000.0 }, 1.0)]
    public void MedianPicksTheMiddleValue(double[] values, double expected)
    {
        Assert.Equal((decimal)expected, ExchangeRateService.Median(values.Select(value => (decimal)value)));
    }

    [Fact]
    public void MedianOfNothingIsAnError()
    {
        Assert.Throws<ArgumentException>(() => ExchangeRateService.Median([]));
    }

    private static StubExchange Exchange(string name, decimal rate) =>
        new(name, () => new BitcoinExchangeRateModel { ExchangeName = name, BitcoinRateInUSD = rate });

    private sealed class StubExchange(string name, Func<BitcoinExchangeRateModel> fetch) : IBitcoinExchangeRateClient
    {
        public string ExchangeName => name;

        public Task<BitcoinExchangeRateModel> GetExchangeRateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(fetch());
    }

    private sealed class FixedCurrencies : ICurrencyExchangeRateClient
    {
        public Task<CurrencyExchangeRateModel> GetCurrencyRateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CurrencyExchangeRateModel
            {
                Date = DateTime.UtcNow,
                Rates = [new() { Currency = "USD", Rate = 1m }, new() { Currency = "BRL", Rate = 5m }]
            });
    }
}
