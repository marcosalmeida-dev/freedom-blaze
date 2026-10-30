using System.Net;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Tests;

public sealed class WebhookDispatcherTests
{
    private static readonly DateTimeOffset Start = GatewayFixture.Start;

    [Fact]
    public async Task AnEventIsPostedSignedForItsEndpoint()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        var delivery = await SettledDeliveryAsync(fixture);

        Assert.Equal(1, await fixture.Dispatcher.DispatchDueAsync(default));

        var received = Assert.Single(fixture.Receiver.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal(GatewayFixture.EndpointUrl, received.Url.AbsoluteUri);
        Assert.Equal(delivery.EventId, received.Id);
        Assert.Equal(fixture.Clock.Now.ToUnixTimeSeconds().ToString(), received.Timestamp);
        Assert.Equal(delivery.Payload, received.Body);
        Assert.True(received.IsSignedWith(endpoint.Secret));

        var stored = Assert.Single(fixture.Db.Deliveries());
        Assert.Equal(WebhookDeliveryState.Delivered, stored.State);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(200, stored.LastStatusCode);
        Assert.Equal(fixture.Clock.Now, stored.DeliveredAtUtc);
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Null(stored.ClaimedUntilUtc);
    }

    [Fact]
    public async Task AFailingEndpointIsRetriedOnScheduleUntilTheDeliveryFails()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        await SettledDeliveryAsync(fixture);
        fixture.Receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("down") };

        for (var attempt = 1; attempt <= WebhookDispatcher.MaxAttempts; attempt++)
        {
            Assert.Equal(1, await fixture.Dispatcher.DispatchDueAsync(default));
            var stored = Assert.Single(fixture.Db.Deliveries());
            Assert.Equal(attempt, stored.Attempts);
            Assert.Equal(500, stored.LastStatusCode);
            Assert.Equal("HTTP 500: down", stored.LastError);

            if (attempt < WebhookDispatcher.MaxAttempts)
            {
                var wait = WebhookDispatcher.RetrySchedule[attempt - 1];
                Assert.Equal(fixture.Clock.Now + wait, stored.NextAttemptAtUtc);
                Assert.Equal(0, await fixture.Dispatcher.DispatchDueAsync(default)); // not due yet
                fixture.Clock.Now += wait;
            }
            else
            {
                Assert.Equal(WebhookDeliveryState.Failed, stored.State);
                Assert.Null(stored.NextAttemptAtUtc);
            }
        }

        Assert.Equal(WebhookDispatcher.MaxAttempts, fixture.Receiver.Received.Count);
        Assert.Single(fixture.Receiver.Received.Select(r => r.Id).Distinct()); // one event, one webhook-id
        Assert.True(fixture.Clock.Now - Start > TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task AnEndpointThatRecoversGetsTheEvent()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        await SettledDeliveryAsync(fixture);
        fixture.Receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await fixture.Dispatcher.DispatchDueAsync(default);
        fixture.Receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        fixture.Clock.Now += WebhookDispatcher.RetrySchedule[0];
        await fixture.Dispatcher.DispatchDueAsync(default);

        var stored = Assert.Single(fixture.Db.Deliveries());
        Assert.Equal(WebhookDeliveryState.Delivered, stored.State);
        Assert.Equal(2, stored.Attempts);
        Assert.Equal(204, stored.LastStatusCode);
    }

    [Theory]
    [InlineData("unreachable", "Could not reach the endpoint")]
    [InlineData("timeout", "The endpoint did not answer in time.")]
    [InlineData("redirect", "HTTP 302")]
    public async Task UnreachableSlowAndRedirectingEndpointsFailTheAttempt(string behaviour, string error)
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        await SettledDeliveryAsync(fixture);
        fixture.Receiver.Respond = behaviour switch
        {
            "unreachable" => _ => throw new HttpRequestException("No such host is known."),
            "timeout" => _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
            _ => _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example/") } },
        };

        await fixture.Dispatcher.DispatchDueAsync(default);

        var stored = Assert.Single(fixture.Db.Deliveries());
        Assert.Equal(WebhookDeliveryState.Pending, stored.State);
        Assert.StartsWith(error, stored.LastError);
        Assert.Single(fixture.Receiver.Received); // a redirect is never followed
    }

    [Fact]
    public async Task ADeliveryClaimedByAnotherInstanceIsNotSentTwice()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var delivery = await SettledDeliveryAsync(fixture);
        using (var db = fixture.Db.CreateDbContext())
            await db.WebhookDeliveries.Where(d => d.Id == delivery.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.ClaimedUntilUtc, fixture.Clock.Now.AddMinutes(2)));

        Assert.Equal(0, await fixture.Dispatcher.DispatchDueAsync(default));
        Assert.Empty(fixture.Receiver.Received);

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3); // that instance died: its claim lapses
        Assert.Equal(1, await fixture.Dispatcher.DispatchDueAsync(default));
        Assert.Single(fixture.Receiver.Received);
    }

    [Fact]
    public async Task ADeliveryForADisabledEndpointFailsWithoutBeingSent()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        await SettledDeliveryAsync(fixture);
        using (var db = fixture.Db.CreateDbContext())
            await db.WebhookEndpoints.Where(e => e.Id == endpoint.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(e => e.IsActive, false));

        await fixture.Dispatcher.DispatchDueAsync(default);

        var stored = Assert.Single(fixture.Db.Deliveries());
        Assert.Equal(WebhookDeliveryState.Failed, stored.State);
        Assert.Equal("The endpoint is disabled.", stored.LastError);
        Assert.Empty(fixture.Receiver.Received);
    }

    [Fact]
    public async Task ARetryIsSignedWithTheEndpointsCurrentSecret()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        await SettledDeliveryAsync(fixture);
        fixture.Receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await fixture.Dispatcher.DispatchDueAsync(default);

        var rotated = await fixture.Endpoints.RotateSecretAsync(fixture.MerchantId, endpoint.Id, default);
        fixture.Receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        fixture.Clock.Now += WebhookDispatcher.RetrySchedule[0];
        await fixture.Dispatcher.DispatchDueAsync(default);

        var retry = fixture.Receiver.Received[^1];
        Assert.True(retry.IsSignedWith(rotated!.Secret));
        Assert.False(retry.IsSignedWith(endpoint.Secret));
    }

    /// <summary>Settles an R$ 40 invoice in the background and returns the delivery it queued.</summary>
    private static async Task<WebhookDelivery> SettledDeliveryAsync(GatewayFixture fixture)
    {
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);
        await fixture.Sweeper.SweepAsync(default);
        return Assert.Single(fixture.Db.Deliveries());
    }
}
