using System.Net;
using System.Text.Json;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;
using FreedomBlaze.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Tests;

public sealed class WebhookEndpointServiceTests
{
    private static readonly DateTimeOffset Start = GatewayFixture.Start;

    [Fact]
    public async Task AnEndpointsSecretIsShownOnceAndStoredEncrypted()
    {
        using var fixture = new GatewayFixture();

        var created = await fixture.Endpoints.CreateAsync(fixture.MerchantId,
            new CreateWebhookEndpointRequest { Url = "https://shop.example/hooks", Description = " Artcana checkout " }, default);
        var listed = await fixture.Endpoints.ListAsync(fixture.MerchantId, default);

        Assert.StartsWith(WebhookSignature.SecretPrefix, created.Secret);
        Assert.Equal("https://shop.example/hooks", created.Url);
        Assert.Equal("Artcana checkout", created.Description);
        Assert.True(created.IsActive);
        Assert.Equal(Start, created.CreatedAtUtc);

        using var db = fixture.Db.CreateDbContext();
        var stored = db.WebhookEndpoints.Single();
        Assert.DoesNotContain(created.Secret, stored.ProtectedSecret);
        Assert.Equal(created.Secret, fixture.Protector.Unprotect(fixture.MerchantId, stored.ProtectedSecret, CredentialPurpose.WebhookSecret));

        var info = Assert.Single(listed);
        Assert.Equal(created.Id, info.Id);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(listed, JsonSerializerOptions.Web), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://shop.example/hooks", "must use HTTPS")]
    [InlineData("https://user:pw@shop.example/hooks", "must not embed credentials")]
    [InlineData("/hooks", "absolute HTTPS URL")]
    [InlineData("https://evil.example/hooks", "is not in LightningInvoices:AllowedWebhookHosts")]
    public async Task UnacceptableUrlsAreRefused(string url, string problem)
    {
        using var fixture = new GatewayFixture(invoiceOptions: new LightningInvoiceOptions { AllowedWebhookHosts = ["shop.example"] });

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.AddEndpointAsync(url));

        Assert.Contains(problem, refused.Message);
        Assert.Empty(await fixture.Endpoints.ListAsync(fixture.MerchantId, default));
    }

    [Fact]
    public async Task PlainHttpIsForLocalDevelopmentOnly()
    {
        using var fixture = new GatewayFixture(webhookOptions: new LightningWebhookOptions { AllowInsecureHttp = true });

        var created = await fixture.AddEndpointAsync("http://127.0.0.1:5400/hooks");

        Assert.Equal("http://127.0.0.1:5400/hooks", created.Url);
    }

    [Fact]
    public async Task WithoutACredentialKeyNoEndpointCanBeRegistered()
    {
        using var fixture = new GatewayFixture(credentialKey: false);

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.AddEndpointAsync());

        Assert.Contains("LightningBackends:CredentialKeys", refused.Message);
    }

    [Fact]
    public async Task AMerchantHasAtMostTenActiveEndpoints()
    {
        using var fixture = new GatewayFixture();
        for (var i = 0; i < WebhookEndpointService.MaxEndpointsPerMerchant; i++)
            await fixture.AddEndpointAsync($"https://shop.example/{i}");

        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.AddEndpointAsync("https://shop.example/one-too-many"));
    }

    [Fact]
    public async Task DisablingAnEndpointFailsItsPendingDeliveriesAndStopsNewOnes()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        var first = await fixture.IssueAsync("order-1");
        fixture.Node.Pay(first.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);
        await fixture.Sweeper.SweepAsync(default);

        var disabled = await fixture.Endpoints.UpdateAsync(fixture.MerchantId, endpoint.Id,
            new UpdateWebhookEndpointRequest { IsActive = false }, default);

        Assert.False(disabled!.IsActive);
        Assert.Equal(fixture.Clock.Now, disabled.DisabledAtUtc);
        var failed = Assert.Single(fixture.Db.Deliveries());
        Assert.Equal(WebhookDeliveryState.Failed, failed.State);
        Assert.Equal("The endpoint was disabled.", failed.LastError);

        var enabled = await fixture.Endpoints.UpdateAsync(fixture.MerchantId, endpoint.Id,
            new UpdateWebhookEndpointRequest { IsActive = true }, default);
        var second = await fixture.IssueAsync("order-2");
        fixture.Node.Pay(second.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(10);
        await fixture.Sweeper.SweepAsync(default);

        Assert.True(enabled!.IsActive);
        Assert.Null(enabled.DisabledAtUtc);
        Assert.Equal(2, fixture.Db.Deliveries().Count);
    }

    [Fact]
    public async Task ATestEventIsSignedSentAndReported()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();

        var delivered = await fixture.Endpoints.SendTestAsync(fixture.MerchantId, endpoint.Id, default);
        fixture.Receiver.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad signature") };
        var refused = await fixture.Endpoints.SendTestAsync(fixture.MerchantId, endpoint.Id, default);

        Assert.True(delivered!.Delivered);
        Assert.Equal(200, delivered.StatusCode);
        Assert.False(refused!.Delivered);
        Assert.Equal("HTTP 401: bad signature", refused.Error);
        var received = fixture.Receiver.Received[0];
        Assert.True(received.IsSignedWith(endpoint.Secret));
        Assert.Equal("webhook.test", received.Json.GetProperty("type").GetString());
        Assert.Empty(fixture.Db.Deliveries()); // tests are not queued or retried
    }

    [Fact]
    public async Task EndpointsAndDeliveriesBelongToOneMerchant()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        var other = fixture.Db.AddMerchant("Other").Id;
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);
        await fixture.Sweeper.SweepAsync(default);
        var delivery = Assert.Single(fixture.Db.Deliveries());

        Assert.Empty(await fixture.Endpoints.ListAsync(other, default));
        Assert.Null(await fixture.Endpoints.UpdateAsync(other, endpoint.Id, new UpdateWebhookEndpointRequest { IsActive = false }, default));
        Assert.Null(await fixture.Endpoints.RotateSecretAsync(other, endpoint.Id, default));
        Assert.Null(await fixture.Endpoints.SendTestAsync(other, endpoint.Id, default));
        Assert.Empty((await fixture.Endpoints.ListDeliveriesAsync(other, new WebhookDeliveryQuery(), default)).Items);
        Assert.Null(await fixture.Endpoints.RedeliverAsync(other, delivery.Id, default));
        Assert.True((await fixture.Endpoints.ListAsync(fixture.MerchantId, default)).Single().IsActive);
    }

    [Fact]
    public async Task TheDeliveryLogPagesNewestFirstAndFilters()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        for (var i = 1; i <= 3; i++)
        {
            var invoice = await fixture.IssueAsync($"order-{i}");
            fixture.Node.Pay(invoice.PaymentHash!);
        }

        fixture.Clock.Now = Start.AddSeconds(5);
        await fixture.Sweeper.SweepAsync(default);
        fixture.Receiver.Respond = received => received.Json.GetProperty("data").GetProperty("externalId").GetString() == "order-2"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK);
        await fixture.Dispatcher.DispatchDueAsync(default);
        var ids = fixture.Db.Deliveries().Select(d => d.Id).ToList();

        var first = await fixture.Endpoints.ListDeliveriesAsync(fixture.MerchantId, new WebhookDeliveryQuery(Limit: 2), default);
        var last = await fixture.Endpoints.ListDeliveriesAsync(fixture.MerchantId, new WebhookDeliveryQuery(Limit: 2, Before: first.NextCursor), default);
        var pending = await fixture.Endpoints.ListDeliveriesAsync(fixture.MerchantId,
            new WebhookDeliveryQuery(State: WebhookDeliveryState.Pending), default);
        var byEndpoint = await fixture.Endpoints.ListDeliveriesAsync(fixture.MerchantId, new WebhookDeliveryQuery(EndpointId: endpoint.Id), default);

        Assert.Equal(new[] { ids[2], ids[1] }, first.Items.Select(d => d.Id).ToArray());
        Assert.Equal(new[] { ids[0] }, last.Items.Select(d => d.Id).ToArray());
        Assert.Null(last.NextCursor);
        var retrying = Assert.Single(pending.Items);
        Assert.Equal(WebhookDeliveryInfo.Pending, retrying.State);
        Assert.Equal(500, retrying.LastStatusCode);
        Assert.Equal(3, byEndpoint.Items.Count);
    }

    [Fact]
    public async Task RedeliveryQueuesTheSameEventAgain()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);
        await fixture.Sweeper.SweepAsync(default);
        await fixture.Dispatcher.DispatchDueAsync(default);
        var original = Assert.Single(fixture.Db.Deliveries());

        var again = await fixture.Endpoints.RedeliverAsync(fixture.MerchantId, original.Id, default);
        await fixture.Dispatcher.DispatchDueAsync(default);

        Assert.NotEqual(original.Id, again!.Id);
        Assert.Equal(original.EventId, again.EventId);
        Assert.Equal(WebhookDeliveryInfo.Pending, again.State);
        Assert.Equal(2, fixture.Receiver.Received.Count);
        Assert.Equal(fixture.Receiver.Received[0].Body, fixture.Receiver.Received[1].Body);
        Assert.Equal(fixture.Receiver.Received[0].Id, fixture.Receiver.Received[1].Id);

        await fixture.Endpoints.UpdateAsync(fixture.MerchantId, endpoint.Id, new UpdateWebhookEndpointRequest { IsActive = false }, default);
        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Endpoints.RedeliverAsync(fixture.MerchantId, original.Id, default));
    }
}
