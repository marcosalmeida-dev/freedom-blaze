using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FreedomBlaze.Clients.BitcoinTracking;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Tests;

public sealed class BitcoinHistoricalPriceTests
{
    private static readonly DateOnly Day = new(2024, 1, 6);

    [Fact]
    public async Task ExactUtcCloseUsesHistoricalBrlFxAndDisclosesItsObservationDate()
    {
        using var fixture = new Fixture(candles: (request, _) => Task.FromResult(Json(new[]
        {
            Candle(Day.AddDays(1), 999_999m), Candle(Day, 43_992.44m), Candle(Day.AddDays(-1), 1m),
        })), fx: (_, _) => Task.FromResult(Fx(Day.AddDays(-1), "BRL", 4.9m)));

        var result = await fixture.Service.GetDailyPriceAsync(Day, " brl ");

        Assert.Equal(BitcoinHistoricalPriceStatus.Available, result.Status);
        Assert.Equal(Day, result.Date);
        Assert.Equal("BRL", result.Currency);
        Assert.Equal(43_992.44m, result.BitcoinUsdClose);
        Assert.Equal(4.9m, result.UsdToCurrencyRate);
        Assert.Equal(215_562.956m, result.PricePerBitcoin);
        Assert.Equal(Day.AddDays(-1), result.FxDate);
        Assert.Contains("daily close", result.BitcoinSource);
        Assert.Contains("historical", result.FxSource);
        var candleRequest = Assert.Single(fixture.CandleRequests);
        Assert.Contains("granularity=86400", candleRequest);
        Assert.Contains("start=2024-01-06T00:00:00Z", candleRequest);
        Assert.Contains("end=2024-01-07T00:00:00Z", candleRequest);
        Assert.Equal("/v2/rate/usd/brl?date=2024-01-06", Assert.Single(fixture.FxRequests));
    }

    [Theory]
    [InlineData("USD", 1)]
    [InlineData("EUR", 2)]
    [InlineData("GBP", 2)]
    [InlineData("AUD", 2)]
    [InlineData("JPY", 2)]
    [InlineData("ZAR", 2)]
    [InlineData("ARS", 2)]
    [InlineData("BRL", 2)]
    public async Task EveryAppCurrencyUsesHistoricalQuotesAndUsdNeedsNoFx(string currency, int requests)
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.GetDailyPriceAsync(Day, currency);

        Assert.Equal(BitcoinHistoricalPriceStatus.Available, result.Status);
        Assert.Equal(currency, result.Currency);
        Assert.Equal(requests, fixture.CandleRequests.Count + fixture.FxRequests.Count);
        Assert.Equal(currency == "USD" ? 110m : 550m, result.PricePerBitcoin);
        if (currency == "USD")
        {
            Assert.Empty(fixture.FxRequests);
            Assert.Null(result.FxDate);
            Assert.Null(result.FxSource);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CurrentAndFutureUtcDaysDoNotPublishAnUnfinishedClose(int dayOffset)
    {
        using var fixture = new Fixture();
        var today = DateOnly.FromDateTime(fixture.Time.GetUtcNow().UtcDateTime);

        var result = await fixture.Service.GetDailyPriceAsync(today.AddDays(dayOffset), "USD");

        Assert.Equal(BitcoinHistoricalPriceStatus.DayNotComplete, result.Status);
        Assert.Null(result.PricePerBitcoin);
        Assert.Empty(fixture.CandleRequests);
        Assert.Empty(fixture.FxRequests);
    }

    [Fact]
    public async Task UnsupportedCurrencyAndDatesBeforeBitcoinDoNotCallProviders()
    {
        using var fixture = new Fixture();

        var unsupported = await fixture.Service.GetDailyPriceAsync(Day, "INVALID");
        var early = await fixture.Service.GetDailyPriceAsync(DateOnly.MinValue, "BRL");

        Assert.Equal(BitcoinHistoricalPriceStatus.UnsupportedCurrency, unsupported.Status);
        Assert.Equal(BitcoinHistoricalPriceStatus.NoData, early.Status);
        Assert.Null(unsupported.PricePerBitcoin);
        Assert.Null(early.PricePerBitcoin);
        Assert.Empty(fixture.CandleRequests);
        Assert.Empty(fixture.FxRequests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[1704585600,100,120,105,999,25]]")]
    public async Task MissingExactDayNeverUsesAnAdjacentOrCurrentPrice(string json)
    {
        using var fixture = new Fixture(candles: (_, _) => Task.FromResult(Text(json)));

        var result = await fixture.Service.GetDailyPriceAsync(Day, "USD");

        Assert.Equal(BitcoinHistoricalPriceStatus.NoData, result.Status);
        Assert.Null(result.PricePerBitcoin);
        Assert.Null(result.BitcoinUsdClose);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[[1704499200,100,120,105,0,25]]")]
    [InlineData("[[1704499200,100,120,105,-1,25]]")]
    [InlineData("[[1704499200,100,120,105,110,25],[1704499200,100,120,105,120,25]]")]
    [InlineData("[[\"wrong timestamp\",100,120,105,110,25]]")]
    public async Task MalformedAndNonpositiveQuotesStayUnavailable(string json)
    {
        using var fixture = new Fixture(candles: (_, _) => Task.FromResult(Text(json)));

        var result = await fixture.Service.GetDailyPriceAsync(Day, "USD");

        Assert.Equal(BitcoinHistoricalPriceStatus.Unavailable, result.Status);
        Assert.Null(result.PricePerBitcoin);
    }

    [Theory]
    [InlineData("GBP", 0, 5)]
    [InlineData("BRL", 1, 5)]
    [InlineData("BRL", -8, 5)]
    [InlineData("BRL", 0, 0)]
    [InlineData("BRL", 0, -1)]
    public async Task WrongCurrencyFutureStaleOrNonpositiveFxCannotProduceAnEstimate(string currency, int dateOffset, int rate)
    {
        using var fixture = new Fixture(fx: (_, _) => Task.FromResult(Fx(Day.AddDays(dateOffset), currency, rate)));

        var result = await fixture.Service.GetDailyPriceAsync(Day, "BRL");

        Assert.Equal(BitcoinHistoricalPriceStatus.Unavailable, result.Status);
        Assert.Null(result.PricePerBitcoin);
        Assert.Null(result.UsdToCurrencyRate);
    }

    [Fact]
    public async Task CachedUsdCloseIsSharedAcrossCurrenciesAndCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(candles: async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(new[] { Candle(Day, 110m) });
        });
        using var caller = new CancellationTokenSource();
        var first = fixture.Service.GetDailyPriceAsync(Day, "USD", caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Service.GetDailyPriceAsync(Day, "BRL");
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();

        var brl = await second;
        var cachedUsd = await fixture.Service.GetDailyPriceAsync(Day, "USD");

        Assert.Equal(550m, brl.PricePerBitcoin);
        Assert.Equal(110m, cachedUsd.PricePerBitcoin);
        Assert.Single(fixture.CandleRequests);
        Assert.Single(fixture.FxRequests);
    }

    [Fact]
    public async Task ConcurrentPageDatesQueueWithinTwoProviderRequests()
    {
        var active = 0;
        var maximum = 0;
        using var fixture = new Fixture(candles: async (request, token) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try
            {
                await Task.Delay(170, token);
                return DefaultCandle(request);
            }
            finally { Interlocked.Decrement(ref active); }
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(offset => fixture.Service.GetDailyPriceAsync(Day.AddDays(offset), "USD")));

        Assert.All(results, result => Assert.Equal(BitcoinHistoricalPriceStatus.Available, result.Status));
        Assert.InRange(maximum, 1, 2);
        Assert.Equal(25, fixture.CandleRequests.Count);
    }

    [Fact]
    public async Task RequestBudgetAndProviderCooldownPreventAdditionalHttpAttempts()
    {
        using var limited = new Fixture(options: new() { MaxProviderRequestsPerMinute = 1 });
        Assert.Equal(BitcoinHistoricalPriceStatus.Available, (await limited.Service.GetDailyPriceAsync(Day, "USD")).Status);
        Assert.Equal(BitcoinHistoricalPriceStatus.RateLimited, (await limited.Service.GetDailyPriceAsync(Day.AddDays(1), "USD")).Status);
        Assert.Single(limited.CandleRequests);
        limited.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(BitcoinHistoricalPriceStatus.Available, (await limited.Service.GetDailyPriceAsync(Day.AddDays(1), "USD")).Status);
        Assert.Equal(2, limited.CandleRequests.Count);

        var calls = 0;
        using var busy = new Fixture(candles: (request, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(DefaultCandle(request));
            var response = Text("provider-only-detail", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        });
        Assert.Equal(BitcoinHistoricalPriceStatus.RateLimited, (await busy.Service.GetDailyPriceAsync(Day, "USD")).Status);
        var duringCooldown = await busy.Service.GetDailyPriceAsync(Day.AddDays(1), "USD");
        Assert.Equal(BitcoinHistoricalPriceStatus.RateLimited, duringCooldown.Status);
        Assert.Null(duringCooldown.PricePerBitcoin);
        Assert.DoesNotContain("provider-only-detail", duringCooldown.ToString());
        Assert.Single(busy.CandleRequests);
        busy.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(BitcoinHistoricalPriceStatus.Available, (await busy.Service.GetDailyPriceAsync(Day.AddDays(1), "USD")).Status);
        Assert.Equal(2, busy.CandleRequests.Count);
    }

    [Fact]
    public async Task SharedProducerTimeoutReturnsUnavailableAndNeverZero()
    {
        using var fixture = new Fixture(candles: async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unexpected completion");
        }, options: new() { RequestTimeout = TimeSpan.FromMilliseconds(100) });

        var result = await fixture.Service.GetDailyPriceAsync(Day, "USD");

        Assert.Equal(BitcoinHistoricalPriceStatus.Unavailable, result.Status);
        Assert.Null(result.PricePerBitcoin);
    }

    [Fact]
    public async Task QuoteCacheExpiresWithoutDependingOnTodaysExchangeRates()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.GetDailyPriceAsync(Day, "BRL");
        fixture.Time.Advance(TimeSpan.FromHours(23));
        var cached = await fixture.Service.GetDailyPriceAsync(Day, "BRL");
        Assert.Equal(first.RetrievedAt, cached.RetrievedAt);
        Assert.Single(fixture.CandleRequests);
        Assert.Single(fixture.FxRequests);
        fixture.Time.Advance(TimeSpan.FromHours(2));

        var refreshed = await fixture.Service.GetDailyPriceAsync(Day, "BRL");

        Assert.Equal(first.PricePerBitcoin, refreshed.PricePerBitcoin);
        Assert.True(refreshed.RetrievedAt > first.RetrievedAt);
        Assert.Equal(2, fixture.CandleRequests.Count);
        Assert.Equal(2, fixture.FxRequests.Count);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int previous;
        do { previous = Volatile.Read(ref maximum); }
        while (value > previous && Interlocked.CompareExchange(ref maximum, value, previous) != previous);
    }

    private static decimal[] Candle(DateOnly date, decimal close) =>
        [new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds(), 100, 120, 105, close, 25];

    private static HttpResponseMessage DefaultCandle(HttpRequestMessage request) =>
        Json(new[] { Candle(DateOnly.ParseExact(Parameter(request, "start")[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture), 110m) });

    private static string Parameter(HttpRequestMessage request, string name) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(part => part.Split('=', 2)).Single(part => part[0] == name)[1];

    private static HttpResponseMessage Fx(DateOnly date, string currency, decimal rate) => Json(new
    {
        date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), @base = "USD", quote = currency, rate,
    });

    private static HttpResponseMessage Json(object body) => Text(JsonSerializer.Serialize(body));

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Fixture : IDisposable
    {
        private readonly BitcoinHistoricalPriceClient _client;
        public Clock Time { get; } = new();
        public ConcurrentQueue<string> CandleRequests { get; } = new();
        public ConcurrentQueue<string> FxRequests { get; } = new();
        public BitcoinHistoricalPriceService Service { get; }

        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? candles = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? fx = null,
            BitcoinHistoricalPriceOptions? options = null)
        {
            var settings = Microsoft.Extensions.Options.Options.Create(options ?? new BitcoinHistoricalPriceOptions());
            var candleHttp = new HttpClient(new Handler(CandleRequests, candles ?? ((request, _) => Task.FromResult(DefaultCandle(request)))))
            {
                BaseAddress = new Uri("https://candles.example/"),
            };
            var fxHttp = new HttpClient(new Handler(FxRequests, fx ?? ((request, _) => Task.FromResult(Fx(
                DateOnly.ParseExact(Parameter(request, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                request.RequestUri!.AbsolutePath.Split('/').Last().ToUpperInvariant(), 5m)))))
            {
                BaseAddress = new Uri("https://fx.example/"),
            };
            _client = new(candleHttp, fxHttp, settings, Time);
            Service = new(_client, settings, Time);
        }

        public void Dispose()
        {
            Service.Dispose();
            _client.Dispose();
        }
    }

    private sealed class Handler(ConcurrentQueue<string> requests,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
            return respond(request, cancellationToken);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 0, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
