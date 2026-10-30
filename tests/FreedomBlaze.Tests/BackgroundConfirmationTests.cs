using System.Net;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using FreedomBlaze.Services.Lightning;
using FreedomBlaze.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Tests;

/// <summary>The gateway confirming invoices nobody asks about, and announcing each settlement once.</summary>
public sealed class BackgroundConfirmationTests
{
    private static readonly DateTimeOffset Start = GatewayFixture.Start;

    [Fact]
    public async Task ANewInvoiceIsFirstCheckedFiveSecondsAfterItIsIssued()
    {
        using var fixture = new GatewayFixture();

        var invoice = await fixture.IssueAsync();

        Assert.Equal(Start.AddSeconds(5), fixture.Stored(invoice.Id).NextCheckAtUtc);
    }

    [Theory]
    [InlineData(5, 10)]                        // a payment is most likely in the first two minutes
    [InlineData(180, 195)]                     // then every 15 s until it expires
    [InlineData(595, 605)]                     // and once right after expiry, not up to 15 s later
    [InlineData(605, 665)]                     // an hour of minutely checks while the node may still answer
    [InlineData(600 + 7200, 600 + 7200 + 600)] // then every ten minutes for a day
    [InlineData(600 + 172_800, 600 + 172_800 + 3600)] // then hourly
    public void ChecksComeOftenWhileAPaymentIsLikelyAndThinOutAfterExpiry(int nowSeconds, int nextSeconds)
    {
        var next = LightningInvoiceSchedule.NextCheck(Start, Start.AddMinutes(10), Start.AddSeconds(nowSeconds));

        Assert.Equal(Start.AddSeconds(nextSeconds), next);
    }

    [Fact]
    public void AWeekAfterExpiryTheSweepGivesUp()
    {
        Assert.Null(LightningInvoiceSchedule.NextCheck(Start, Start.AddMinutes(10), Start.AddMinutes(10).AddDays(7)));
    }

    [Fact]
    public async Task NothingIsCheckedBeforeItIsDue()
    {
        using var fixture = new GatewayFixture();
        await fixture.IssueAsync();

        Assert.Equal(0, await fixture.Sweeper.SweepAsync(default));
        Assert.Equal("/createinvoice", Assert.Single(fixture.Node.Requests).Path);
    }

    [Fact]
    public async Task APaymentNobodyAskedAboutIsRecordedAndAnnounced()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);

        Assert.Equal(1, await fixture.Sweeper.SweepAsync(default));

        var stored = fixture.Stored(invoice.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, stored.Status);
        Assert.Equal(10_000, stored.ReceivedSat);
        Assert.Null(stored.NextCheckAtUtc);

        var delivery = Assert.Single(fixture.Db.Deliveries());
        Assert.Equal(WebhookEventTypes.InvoicePaid, delivery.EventType);
        Assert.Equal(invoice.Id, delivery.InvoiceId);
        Assert.Equal(WebhookDeliveryState.Pending, delivery.State);
        Assert.Equal(Start.AddSeconds(5), delivery.NextAttemptAtUtc);

        var body = System.Text.Json.JsonDocument.Parse(delivery.Payload).RootElement;
        Assert.Equal(delivery.EventId, body.GetProperty("id").GetString());
        Assert.Equal("invoice.paid", body.GetProperty("type").GetString());
        Assert.Equal(invoice.PaymentHash, body.GetProperty("data").GetProperty("paymentHash").GetString());
        Assert.Equal("paid", body.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("order-123", body.GetProperty("data").GetProperty("externalId").GetString());
        Assert.Equal(10_000, body.GetProperty("data").GetProperty("receivedSat").GetInt64());
    }

    [Fact]
    public async Task AnUnpaidInvoiceIsAskedAboutAgainOnSchedule()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Clock.Now = Start.AddSeconds(5);

        await fixture.Sweeper.SweepAsync(default);

        var stored = fixture.Stored(invoice.Id);
        Assert.Equal(LightningInvoiceStatus.Pending, stored.Status);
        Assert.Equal(Start.AddSeconds(10), stored.NextCheckAtUtc);
        Assert.Empty(fixture.Db.Deliveries());
        Assert.Equal($"/payments/incoming/{invoice.PaymentHash}", fixture.Node.Requests[^1].Path);
    }

    [Fact]
    public async Task ExpiryIsRecordedAndAnnounced()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Expire(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddMinutes(10).AddSeconds(5);

        await fixture.Sweeper.SweepAsync(default);

        Assert.Equal(LightningInvoiceStatus.Expired, fixture.Stored(invoice.Id).Status);
        Assert.Equal(WebhookEventTypes.InvoiceExpired, Assert.Single(fixture.Db.Deliveries()).EventType);
    }

    [Fact]
    public async Task ANodeThatIsDownIsAskedAgainLater()
    {
        using var fixture = new GatewayFixture();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Node.Override = _ => FakePhoenixd.Text(HttpStatusCode.BadGateway, "tunnel down");
        fixture.Clock.Now = Start.AddSeconds(5);

        await fixture.Sweeper.SweepAsync(default);

        Assert.Equal(LightningInvoiceStatus.Pending, fixture.Stored(invoice.Id).Status);
        Assert.Equal(Start.AddSeconds(10), fixture.Stored(invoice.Id).NextCheckAtUtc);

        fixture.Node.Override = null;
        fixture.Clock.Now = Start.AddSeconds(10);
        await fixture.Sweeper.SweepAsync(default);

        Assert.Equal(LightningInvoiceStatus.Paid, fixture.Stored(invoice.Id).Status);
    }

    [Fact]
    public async Task APaymentTheNodeCannotProveIsNeitherRecordedNorAnnounced()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Override = path => path.Contains("/payments/incoming/", StringComparison.Ordinal)
            ? FakePhoenixd.Json(new
            {
                paymentHash = invoice.PaymentHash, preimage = new string('0', 64), isPaid = true, isExpired = false,
                requestedSat = 10_000, receivedSat = 10_000, fees = 0, completedAt = 1791853300000, createdAt = 1791853243502,
            })
            : null;
        fixture.Clock.Now = Start.AddSeconds(5);

        await fixture.Sweeper.SweepAsync(default);

        Assert.Equal(LightningInvoiceStatus.Pending, fixture.Stored(invoice.Id).Status);
        Assert.Empty(fixture.Db.Deliveries());
    }

    [Fact]
    public async Task ASettlementFoundTwiceIsRecordedAndAnnouncedOnce()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);

        // Two checkers holding the same pending copy: the sweep and a client's read, say.
        var first = await fixture.Refresher.RefreshAsync(Copy(invoice), fixture.Backend, default);
        var second = await fixture.Refresher.RefreshAsync(Copy(invoice), fixture.Backend, default);

        Assert.Equal(InvoiceRefreshOutcome.Settled, first.Outcome);
        Assert.Equal(InvoiceRefreshOutcome.Settled, second.Outcome);
        Assert.Equal(LightningInvoiceStatus.Paid, second.Invoice.Status);
        Assert.Single(fixture.Db.Deliveries());
    }

    [Fact]
    public async Task AClientsReadRecordsTheSettlementForTheSweepToo()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);

        var read = await fixture.Invoices.GetAsync(fixture.MerchantId, invoice.PaymentHash!, default);
        fixture.Clock.Now = Start.AddSeconds(5);

        Assert.Equal(LightningInvoiceStatus.Paid, read.Invoice!.Status);
        Assert.Equal(0, await fixture.Sweeper.SweepAsync(default));
        Assert.Equal(WebhookEventTypes.InvoicePaid, Assert.Single(fixture.Db.Deliveries()).EventType);
    }

    [Fact]
    public async Task OnlyActiveEndpointsAreSentTheEvent()
    {
        using var fixture = new GatewayFixture();
        var kept = await fixture.AddEndpointAsync("https://shop.example/a");
        var dropped = await fixture.AddEndpointAsync("https://shop.example/b");
        await fixture.Endpoints.UpdateAsync(fixture.MerchantId, dropped.Id, new UpdateWebhookEndpointRequest { IsActive = false }, default);
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);

        await fixture.Sweeper.SweepAsync(default);

        Assert.Equal(kept.Id, Assert.Single(fixture.Db.Deliveries()).EndpointId);
    }

    [Fact]
    public async Task WithoutEndpointsASettlementIsStillRecorded()
    {
        using var fixture = new GatewayFixture();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);

        await fixture.Sweeper.SweepAsync(default);

        Assert.Equal(LightningInvoiceStatus.Paid, fixture.Stored(invoice.Id).Status);
        Assert.Empty(fixture.Db.Deliveries());
    }

    [Fact]
    public async Task AnInvoiceStillUnconfirmedAWeekAfterExpiryIsLeftAlone()
    {
        using var fixture = new GatewayFixture();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Override = _ => FakePhoenixd.Text(HttpStatusCode.BadGateway, "gone");
        var late = Start.AddMinutes(10).AddDays(8);
        using (var db = fixture.Db.CreateDbContext())
            await db.LightningInvoices.Where(i => i.Id == invoice.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.NextCheckAtUtc, late));
        fixture.Clock.Now = late;

        Assert.Equal(1, await fixture.Sweeper.SweepAsync(default));
        fixture.Clock.Now = late.AddDays(30);
        Assert.Equal(0, await fixture.Sweeper.SweepAsync(default));

        var stored = fixture.Stored(invoice.Id);
        Assert.Equal(LightningInvoiceStatus.Pending, stored.Status);
        Assert.Null(stored.NextCheckAtUtc);
    }

    [Fact]
    public async Task OneWorkerRoundRecordsAPaymentAndDeliversItsWebhook()
    {
        using var fixture = new GatewayFixture();
        var endpoint = await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = Start.AddSeconds(5);

        await fixture.Worker.RunOnceAsync(default);

        Assert.Equal(LightningInvoiceStatus.Paid, fixture.Stored(invoice.Id).Status);
        var received = Assert.Single(fixture.Receiver.Received);
        Assert.True(received.IsSignedWith(endpoint.Secret));
        Assert.Equal("invoice.paid", received.Json.GetProperty("type").GetString());
        Assert.Equal(WebhookDeliveryState.Delivered, Assert.Single(fixture.Db.Deliveries()).State);
    }

    private static LightningInvoice Copy(LightningInvoice invoice) => new()
    {
        Id = invoice.Id, MerchantId = invoice.MerchantId, ApiKeyId = invoice.ApiKeyId, BackendId = invoice.BackendId,
        Status = invoice.Status, PaymentHash = invoice.PaymentHash, Bolt11 = invoice.Bolt11, ExternalId = invoice.ExternalId,
        Description = invoice.Description, Amount = invoice.Amount, Currency = invoice.Currency, AmountSat = invoice.AmountSat,
        BitcoinPrice = invoice.BitcoinPrice, BitcoinPriceUsd = invoice.BitcoinPriceUsd, RateSources = invoice.RateSources,
        QuotedAtUtc = invoice.QuotedAtUtc, ExpiresAtUtc = invoice.ExpiresAtUtc, CreatedAtUtc = invoice.CreatedAtUtc,
        NextCheckAtUtc = invoice.NextCheckAtUtc,
    };
}
