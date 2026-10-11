using System.Net;
using FreedomBlaze.Clients.BitcoinTracking;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Options;

namespace FreedomBlaze.Tests;

public sealed class BitcoinFeeRecommendationsTests
{
    [Fact]
    public async Task RecommendationsMapMempoolPrioritiesAndPreserveFractionalRates()
    {
        var time = new TrackingTestTimeProvider();
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Json(new EsploraFeeRecommendations
        {
            MinimumFee = 0.25m, EconomyFee = 1.25m, HourFee = 2.5m, HalfHourFee = 3.75m, FastestFee = 5.125m,
        }));
        using var client = TrackingTestData.Client(handler, time: time);
        using var service = TrackingTestData.Service(client, time: time);

        var result = await service.GetFeeRecommendationsAsync();

        Assert.Equal(1.25m, result.NoPrioritySatPerVByte);
        Assert.Equal(2.5m, result.LowPrioritySatPerVByte);
        Assert.Equal(3.75m, result.MediumPrioritySatPerVByte);
        Assert.Equal(5.125m, result.HighPrioritySatPerVByte);
        Assert.Equal(0.25m, result.MinimumSatPerVByte);
        Assert.Equal(time.GetUtcNow(), result.UpdatedAt);
        Assert.Equal("v1/fees/precise", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task PortalSubSatoshiRatesComeFromPreciseRecommendationsInsteadOfRoundedLegacyRates()
    {
        using var handler = new TrackingRoutingHandler(path => TrackingTestData.Text(path == "v1/fees/precise"
            ? "{\"fastestFee\":1,\"halfHourFee\":0.572,\"hourFee\":0.284,\"economyFee\":0.2,\"minimumFee\":0.1}"
            : "{\"fastestFee\":1,\"halfHourFee\":1,\"hourFee\":1,\"economyFee\":1,\"minimumFee\":1}"));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.GetFeeRecommendationsAsync();

        Assert.Equal(0.2m, result.NoPrioritySatPerVByte);
        Assert.Equal(0.284m, result.LowPrioritySatPerVByte);
        Assert.Equal(0.572m, result.MediumPrioritySatPerVByte);
        Assert.Equal(1m, result.HighPrioritySatPerVByte);
        Assert.Equal(0.1m, result.MinimumSatPerVByte);
        Assert.Equal("v1/fees/precise", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task OlderProvidersFallBackToLegacyRecommendationsOnlyWhenPreciseIsMissing()
    {
        using var handler = new TrackingRoutingHandler(path => path == "v1/fees/precise"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : TrackingTestData.Json(Recommendations()));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.GetFeeRecommendationsAsync();

        Assert.Equal(2m, result.NoPrioritySatPerVByte);
        Assert.Equal(5m, result.HighPrioritySatPerVByte);
        Assert.Equal(new[] { "v1/fees/precise", "v1/fees/recommended" }, handler.Paths.ToArray());
        Assert.Same(result, await service.GetFeeRecommendationsAsync());
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task LegacyFallbackIsCountedAgainstTheSameProviderBudget()
    {
        var options = new BitcoinTrackingOptions { MaxProviderRequestsPerMinute = 1 };
        using var handler = new TrackingRoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = TrackingTestData.Client(handler, options);
        using var service = TrackingTestData.Service(client, options);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());

        Assert.Equal(BitcoinTrackingError.RateLimited, error.Error);
        Assert.Equal("v1/fees/precise", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task EqualPriorityRatesAreValidOnAQuietNetwork()
    {
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Json(new EsploraFeeRecommendations
        {
            MinimumFee = 1, EconomyFee = 1, HourFee = 1, HalfHourFee = 1, FastestFee = 1,
        }));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.GetFeeRecommendationsAsync();

        Assert.Equal(1m, result.NoPrioritySatPerVByte);
        Assert.Equal(1m, result.HighPrioritySatPerVByte);
    }

    [Fact]
    public async Task ThirtySecondSnapshotIsSharedAndRefreshesItsRatesAndTimestamp()
    {
        var time = new TrackingTestTimeProvider();
        var high = 5m;
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Json(Recommendations() with { FastestFee = high }));
        using var client = TrackingTestData.Client(handler, time: time);
        using var service = TrackingTestData.Service(client, time: time);

        var first = await service.GetFeeRecommendationsAsync();
        high = 9;
        time.Advance(TimeSpan.FromSeconds(29));
        var cached = await service.GetFeeRecommendationsAsync();
        Assert.Same(first, cached);
        Assert.Single(handler.Paths);

        time.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await service.GetFeeRecommendationsAsync();

        Assert.Equal(5m, first.HighPrioritySatPerVByte);
        Assert.Equal(9m, refreshed.HighPrioritySatPerVByte);
        Assert.Equal(time.GetUtcNow(), refreshed.UpdatedAt);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task OneCancelledReaderDoesNotCancelTheSharedNetworkRecommendationRequest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TrackingRoutingHandler(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return TrackingTestData.Json(Recommendations());
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);
        using var cancellation = new CancellationTokenSource();

        var cancelledReader = service.GetFeeRecommendationsAsync(cancellation.Token);
        var otherReader = service.GetFeeRecommendationsAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledReader);
        release.TrySetResult();

        Assert.Equal(5m, (await otherReader).HighPrioritySatPerVByte);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task RecommendationRequestsUseTheSameGlobalProviderBudgetAsLookups()
    {
        var options = new BitcoinTrackingOptions { MaxProviderRequestsPerMinute = 1 };
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Json(Recommendations()));
        using var client = TrackingTestData.Client(handler, options);
        using var service = TrackingTestData.Service(client, options);

        var first = await service.GetFeeRecommendationsAsync();
        Assert.Same(first, await service.GetFeeRecommendationsAsync());
        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));

        Assert.Equal(BitcoinTrackingError.RateLimited, error.Error);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, BitcoinTrackingError.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, BitcoinTrackingError.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BitcoinTrackingError.Unavailable)]
    public async Task UnsupportedOrFailedProvidersUseStableErrorsWithoutRetryingFailures(HttpStatusCode status,
        BitcoinTrackingError expected)
    {
        using var handler = new TrackingRoutingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("Private provider diagnostics"),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());

        Assert.Equal(expected, error.Error);
        Assert.Equal(expected.ToString(), error.Message);
        if (status == HttpStatusCode.NotFound)
            Assert.Equal(new[] { "v1/fees/precise", "v1/fees/recommended" }, handler.Paths.ToArray());
        else
            Assert.Equal("v1/fees/precise", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task AnUnsupportedRecommendationEndpointDoesNotCoolDownValidTransactionTracking()
    {
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "v1/fees/precise" or "v1/fees/recommended" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "blocks/tip/height" => TrackingTestData.Text("100"),
            _ => TrackingTestData.Json(TrackingTestData.Transaction()),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var feeError = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());
        var transaction = await service.LookupAsync(TrackingTestData.TxId);

        Assert.Equal(BitcoinTrackingError.Unavailable, feeError.Error);
        Assert.Equal(TrackingTestData.TxId, transaction.Transaction!.TxId);
        Assert.Equal(500L, transaction.Transaction.FeeSats);
        Assert.Equal(4, handler.Paths.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"fastestFee\":5,\"halfHourFee\":4,\"hourFee\":3,\"minimumFee\":1}")]
    [InlineData("{\"fastestFee\":5,\"halfHourFee\":4,\"hourFee\":3,\"economyFee\":2,\"minimumFee\":null}")]
    [InlineData("{\"fastestFee\":1e40,\"halfHourFee\":4,\"hourFee\":3,\"economyFee\":2,\"minimumFee\":1}")]
    public async Task IncompleteOrNonNumericSuccessfulPayloadsAreUnavailable(string body)
    {
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Text(body));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());

        Assert.Equal(BitcoinTrackingError.Unavailable, error.Error);
        Assert.Equal("v1/fees/precise", Assert.Single(handler.Paths));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("economyBelowMinimum")]
    [InlineData("lowBelowEconomy")]
    [InlineData("mediumBelowLow")]
    [InlineData("highBelowMedium")]
    public async Task InvalidPriorityRatesAreUnavailableInsteadOfFreeFeesOrReorderedPriorities(string failure)
    {
        var fees = Recommendations();
        fees = failure switch
        {
            "negative" => fees with { MinimumFee = -1 },
            "zero" => fees with { MinimumFee = 0 },
            "economyBelowMinimum" => fees with { EconomyFee = 0.5m },
            "lowBelowEconomy" => fees with { HourFee = 1.5m },
            "mediumBelowLow" => fees with { HalfHourFee = 2.5m },
            _ => fees with { FastestFee = 3.5m },
        };
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Json(fees));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());

        Assert.Equal(BitcoinTrackingError.Unavailable, error.Error);
        Assert.Equal("v1/fees/precise", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task AFailedRefreshDoesNotReturnAnOldRecommendationAsCurrentAndCanRecover()
    {
        var time = new TrackingTestTimeProvider();
        var available = true;
        using var handler = new TrackingRoutingHandler(_ => available
            ? TrackingTestData.Json(Recommendations())
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = TrackingTestData.Client(handler, time: time);
        using var service = TrackingTestData.Service(client, time: time);

        var first = await service.GetFeeRecommendationsAsync();
        time.Advance(TimeSpan.FromSeconds(31));
        available = false;
        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());
        await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.GetFeeRecommendationsAsync());
        Assert.Equal(2, handler.Paths.Count);

        available = true;
        time.Advance(TimeSpan.FromSeconds(6));
        var recovered = await service.GetFeeRecommendationsAsync();

        Assert.Equal(BitcoinTrackingError.Unavailable, error.Error);
        Assert.NotEqual(first.UpdatedAt, recovered.UpdatedAt);
        Assert.Equal(time.GetUtcNow(), recovered.UpdatedAt);
        Assert.Equal(3, handler.Paths.Count);
    }

    private static EsploraFeeRecommendations Recommendations() => new()
    {
        MinimumFee = 1, EconomyFee = 2, HourFee = 3, HalfHourFee = 4, FastestFee = 5,
    };
}
