using System.Net;
using System.Net.Http.Headers;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Options;
using OpenTelemetry;

namespace FreedomBlaze.Tests;

public sealed class EsploraClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound, BitcoinTrackingError.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, BitcoinTrackingError.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BitcoinTrackingError.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, BitcoinTrackingError.Unavailable)]
    public async Task ProviderErrorsHaveStableCodesWithoutProviderBody(HttpStatusCode status, BitcoinTrackingError expected)
    {
        using var handler = new TrackingRoutingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent($"Private provider details for {TrackingTestData.TxId}"),
        });
        using var client = TrackingTestData.Client(handler);

        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTransactionAsync(TrackingTestData.TxId, default));

        Assert.Equal(expected, exception.Error);
        Assert.Equal(expected.ToString(), exception.Message);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("<html>Upstream error</html>")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task MalformedSuccessfulPayloadIsUnavailable(string body)
    {
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Text(body));
        using var client = TrackingTestData.Client(handler);

        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTransactionAsync(TrackingTestData.TxId, default));

        Assert.Equal(BitcoinTrackingError.Unavailable, exception.Error);
    }

    [Theory]
    [InlineData("not-a-height")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public async Task InvalidTipHeightIsUnavailable(string body)
    {
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Text(body));
        using var client = TrackingTestData.Client(handler);

        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));

        Assert.Equal(BitcoinTrackingError.Unavailable, exception.Error);
    }

    [Fact]
    public async Task GlobalBudgetCountsAllPathsAndRecoversInTheNextMinute()
    {
        var time = new TrackingTestTimeProvider();
        var options = new BitcoinTrackingOptions { MaxProviderRequestsPerMinute = 2 };
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Text("102"));
        using var client = TrackingTestData.Client(handler, options, time);

        await client.GetTipHeightAsync(default);
        await client.GetTipHeightAsync(default);
        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(102, await client.GetTipHeightAsync(default));

        Assert.Equal(BitcoinTrackingError.RateLimited, exception.Error);
        Assert.Equal(3, handler.Paths.Count);
    }

    [Fact]
    public async Task RetryAfterSuppressesRequestsUntilTheProviderCooldownExpires()
    {
        var time = new TrackingTestTimeProvider();
        var reads = 0;
        using var handler = new TrackingRoutingHandler(_ =>
        {
            if (++reads > 1) return TrackingTestData.Text("102");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        });
        using var client = TrackingTestData.Client(handler, time: time);

        await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));
        var blocked = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));
        Assert.Single(handler.Paths);
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(102, await client.GetTipHeightAsync(default));

        Assert.Equal(BitcoinTrackingError.RateLimited, blocked.Error);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task NetworkErrorIsUnavailableAndDoesNotRetry()
    {
        using var handler = new TrackingRoutingHandler(_ => throw new HttpRequestException("Private transport failure"));
        using var client = TrackingTestData.Client(handler);

        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));

        Assert.Equal(BitcoinTrackingError.Unavailable, exception.Error);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task ConcurrentProviderWorkIsBoundedWithoutQueuingAnotherLookup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TrackingRoutingHandler(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return TrackingTestData.Text("102");
        });
        using var client = TrackingTestData.Client(handler, new BitcoinTrackingOptions { MaxConcurrentRequests = 2 });

        var first = client.GetTipHeightAsync(default);
        var second = client.GetTipHeightAsync(default);
        var rejected = await Assert.ThrowsAsync<BitcoinTrackingException>(() => client.GetTipHeightAsync(default));
        Assert.Equal(BitcoinTrackingError.RateLimited, rejected.Error);
        Assert.Equal(2, handler.Paths.Count);
        release.TrySetResult();
        Assert.Equal(new[] { 102, 102 }, await Task.WhenAll(first, second));
        Assert.Equal(102, await client.GetTipHeightAsync(default));
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var handler = new TrackingRoutingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TrackingTestData.Text("102");
        });
        using var client = TrackingTestData.Client(handler);
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetTipHeightAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RequestInstrumentationIsSuppressedDuringSensitiveOutboundCalls()
    {
        using var handler = new TrackingRoutingHandler(_ =>
        {
            Assert.True(Sdk.SuppressInstrumentation);
            return TrackingTestData.Text("102");
        });
        using var client = TrackingTestData.Client(handler);

        Assert.Equal(102, await client.GetTipHeightAsync(default));
        Assert.False(Sdk.SuppressInstrumentation);
    }
}
