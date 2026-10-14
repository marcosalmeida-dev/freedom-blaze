using System.Net;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using FreedomBlaze.Services.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Tests;

/// <summary>
/// Drives the invoice service against fake phoenixd nodes that sign real BOLT11 invoices, and the real
/// schema (SQLite). Artcana issues on the operator's node; the other merchant on its own phoenixd. Rates
/// are 80,000 USD per bitcoin and 5 BRL per dollar, so R$ 40 is exactly 10,000 sats.
/// </summary>
public sealed class LightningInvoiceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IssuesAndRecordsAnInvoiceForTheQuotedSats()
    {
        using var fixture = new Fixture();

        var result = await fixture.Create(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "brl", ExternalId = "order-123", Description = "Artcana pedido 123"
        });

        Assert.Equal(InvoiceCreationOutcome.Created, result.Outcome);
        var invoice = Assert.Single(fixture.Db.Invoices());
        Assert.Equal(LightningInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(result.Invoice!.PaymentHash, invoice.PaymentHash);
        Assert.Equal(fixture.Operator[invoice.PaymentHash!].Bolt11, invoice.Bolt11);
        Assert.Equal(fixture.MerchantId, invoice.MerchantId);
        Assert.Equal(fixture.KeyId, invoice.ApiKeyId);
        Assert.Equal(fixture.Backend.Id, invoice.BackendId);
        Assert.Equal(10_000, invoice.AmountSat);
        Assert.Equal(40m, invoice.Amount);
        Assert.Equal("BRL", invoice.Currency);
        Assert.Equal(400_000m, invoice.BitcoinPrice);
        Assert.Equal(80_000m, invoice.BitcoinPriceUsd);
        Assert.Equal(3, invoice.RateSources);
        Assert.Equal(Now, invoice.QuotedAtUtc);
        Assert.Equal(Now.AddSeconds(600), invoice.ExpiresAtUtc);
        Assert.Equal(Now, invoice.CreatedAtUtc);
        Assert.Equal("order-123", invoice.ExternalId);
        Assert.Equal("Artcana pedido 123", invoice.Description);
        Assert.Null(invoice.IdempotencyKey);

        var request = Assert.Single(fixture.Operator.Requests);
        Assert.Equal("/createinvoice", request.Path);
        Assert.Contains("amountSat=10000", request.Body);
        Assert.Contains($"externalId=m{fixture.MerchantId}%3Aorder-123", request.Body);
        Assert.Contains("expirySeconds=600", request.Body);
        Assert.Contains("description=Artcana+pedido+123", request.Body);
        Assert.DoesNotContain("webhookUrl", request.Body);
        Assert.Empty(fixture.Other.Requests);
    }

    [Fact]
    public async Task InvoiceWithoutReferenceIsDescribedByItsAmountAndRoundedUp()
    {
        using var fixture = new Fixture();

        var result = await fixture.Create(new CreateLightningInvoiceRequest
        {
            Amount = 10.0001m, Currency = "BRL", ExpirySeconds = 120
        });

        Assert.Equal(2_501, result.Invoice!.AmountSat); // rounded up, never down
        Assert.Null(result.Invoice.ExternalId);
        Assert.Equal(Now.AddSeconds(120), result.Invoice.ExpiresAtUtc);
        var body = Assert.Single(fixture.Operator.Requests).Body;
        Assert.Contains($"externalId=m{fixture.MerchantId}%3A&", body);
        Assert.Contains("description=Payment+of+10.0001+BRL", body);
        Assert.Contains("expirySeconds=120", body);
    }

    [Theory]
    [InlineData("http://artcana.com.br/api/payments/lightning/webhook")]
    [InlineData("https://user:secret@artcana.com.br/hook")]
    [InlineData("/relative/hook")]
    public async Task UnsafeWebhooksAreRefusedBeforeAnythingIsReservedOrIssued(string webhookUrl)
    {
        using var fixture = new Fixture();

        var result = await fixture.Create(
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", WebhookUrl = webhookUrl }, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.Invalid, result.Outcome);
        Assert.Empty(fixture.Db.Invoices());
        Assert.Empty(fixture.Operator.Requests);
    }

    [Fact]
    public async Task WebhookHostsAreLimitedToTheAllowList()
    {
        using var fixture = new Fixture(settings: new LightningInvoiceOptions { AllowedWebhookHosts = ["artcana.com.br"] });

        var refused = await fixture.Create(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "BRL", WebhookUrl = "https://evil.example/hook"
        });
        var accepted = await fixture.Create(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "BRL", WebhookUrl = "https://ARTCANA.com.br/api/payments/lightning/webhook"
        });

        Assert.Equal(InvoiceCreationOutcome.Invalid, refused.Outcome);
        Assert.Equal(InvoiceCreationOutcome.Created, accepted.Outcome);
        Assert.Equal("https://ARTCANA.com.br/api/payments/lightning/webhook", accepted.Invoice!.WebhookUrl);
        Assert.Contains("webhookUrl=https%3A%2F%2FARTCANA.com.br", Assert.Single(fixture.Operator.Requests).Body);
    }

    [Fact]
    public async Task AmountsAboveTheCeilingAreRefused()
    {
        using var fixture = new Fixture(settings: new LightningInvoiceOptions { MaxAmountSat = 5_000 });

        var result = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" });

        Assert.Equal(InvoiceCreationOutcome.Invalid, result.Outcome);
        Assert.Contains("5000 sat limit", result.Message);
        Assert.Empty(fixture.Db.Invoices());
        Assert.Empty(fixture.Operator.Requests);
    }

    [Theory]
    [InlineData(0, "BRL")]
    [InlineData(-1, "BRL")]
    [InlineData(10, "XYZ")]
    public async Task BadAmountsAndCurrenciesAreRefused(double amount, string currency)
    {
        using var fixture = new Fixture();

        var result = await fixture.Create(new CreateLightningInvoiceRequest { Amount = (decimal)amount, Currency = currency });

        Assert.Equal(InvoiceCreationOutcome.Invalid, result.Outcome);
        Assert.Empty(fixture.Db.Invoices());
        Assert.Empty(fixture.Operator.Requests);
    }

    [Fact]
    public async Task UntrustworthyPricesAreNotInvoiced()
    {
        using var fixture = new Fixture(rates: Rates(sourceCount: 1));

        var result = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.PriceUnavailable, result.Outcome);
        Assert.Empty(fixture.Db.Invoices());
        Assert.Empty(fixture.Operator.Requests);
    }

    [Fact]
    public async Task AMerchantWithoutABackendCannotIssue()
    {
        using var fixture = new Fixture();
        var newcomer = fixture.Db.AddMerchant("Newcomer").Id;

        var result = await fixture.Service.CreateAsync(new InvoiceIssuer(newcomer, null),
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, "attempt-1", default);

        Assert.Equal(InvoiceCreationOutcome.BackendUnavailable, result.Outcome);
        Assert.Equal("No Lightning backend is configured for this merchant.", result.Message);
        Assert.Empty(fixture.Db.Invoices());
        Assert.Empty(fixture.Operator.Requests);
    }

    [Fact]
    public async Task ANodeFailureFreesTheIdempotencyKeyForARetry()
    {
        using var fixture = new Fixture();
        var request = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" };
        fixture.Operator.Override = _ => FakePhoenixd.Text(HttpStatusCode.InternalServerError, "boom");

        var failed = await fixture.Create(request, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.BackendFailure, failed.Outcome);
        Assert.Contains("HTTP 500 boom", failed.Message);
        Assert.Empty(fixture.Db.Invoices());

        fixture.Operator.Override = null;
        var retried = await fixture.Create(request, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.Created, retried.Outcome);
        Assert.Equal(LightningInvoiceStatus.Pending, Assert.Single(fixture.Db.Invoices()).Status);
    }

    [Fact]
    public async Task ABackendIsPinnedOnItsFirstUse()
    {
        using var fixture = new Fixture(pinned: false);

        var result = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" });

        Assert.Equal(InvoiceCreationOutcome.Created, result.Outcome);
        Assert.Equal(new[] { "/getinfo", "/createinvoice" }, fixture.Operator.Requests.Select(r => r.Path).ToArray());
        var backend = fixture.StoredBackend(fixture.Backend.Id);
        Assert.Equal(fixture.Operator.NodeId, backend.NodeId);
        Assert.Equal("mainnet", backend.Chain);
        Assert.True(backend.IsActive);
    }

    [Fact]
    public async Task InvoicesNotSignedByThePinnedNodeAreRefused()
    {
        using var fixture = new Fixture();
        var pinnedKey = fixture.Operator.NodeKey;
        fixture.Operator.NodeKey = Bolt11TestInvoices.NewKey(); // another node now answers at the operator's address
        var request = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" };

        var refused = await fixture.Create(request, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.BackendFailure, refused.Outcome);
        Assert.Contains($"not the pinned {Bolt11TestInvoices.NodeId(pinnedKey)}", refused.Message);
        Assert.Empty(fixture.Db.Invoices()); // nothing handed out, and the key is free again

        fixture.Operator.NodeKey = pinnedKey;
        Assert.Equal(InvoiceCreationOutcome.Created, (await fixture.Create(request, "attempt-1")).Outcome);
    }

    [Theory]
    [InlineData("amount", "asks for 9999000 msat instead of 10000000")]
    [InlineData("hash", "payment hash is not the one the node reported")]
    [InlineData("expiry", "stays payable for 3600 s instead of 600 s")]
    [InlineData("garbage", "not a valid BOLT11 invoice")]
    public async Task InvoicesThatDoNotMatchTheRequestAreRefused(string tamper, string reason)
    {
        using var fixture = new Fixture();
        var node = fixture.Operator;
        node.Override = path => path.EndsWith("/createinvoice", StringComparison.Ordinal) ? Tampered(node, tamper) : null;

        var result = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" });

        Assert.Equal(InvoiceCreationOutcome.BackendFailure, result.Outcome);
        Assert.Contains(reason, result.Message);
        Assert.Empty(fixture.Db.Invoices());
    }

    [Fact]
    public async Task ARetryWithTheSameKeyGetsTheOriginalInvoice()
    {
        using var fixture = new Fixture();

        var first = await fixture.Create(
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", ExternalId = "order-123" }, "order-123-attempt-1");
        var retry = await fixture.Create(
            new CreateLightningInvoiceRequest { Amount = 40.00m, Currency = "brl", ExternalId = "order-123" }, "order-123-attempt-1");

        Assert.Equal(InvoiceCreationOutcome.Created, first.Outcome);
        Assert.Equal(InvoiceCreationOutcome.Replayed, retry.Outcome);
        Assert.Equal(first.Invoice!.PaymentHash, retry.Invoice!.PaymentHash);
        Assert.Equal(first.Invoice.Bolt11, retry.Invoice.Bolt11);
        Assert.Equal("order-123-attempt-1", Assert.Single(fixture.Db.Invoices()).IdempotencyKey);
        Assert.Single(fixture.Operator.Requests);
    }

    [Fact]
    public async Task AKeyReusedForADifferentRequestIsRefused()
    {
        using var fixture = new Fixture();

        await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, "attempt-1");
        var reused = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 41m, Currency = "BRL" }, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.IdempotencyMismatch, reused.Outcome);
        Assert.Null(reused.Invoice);
        Assert.Single(fixture.Db.Invoices());
        Assert.Single(fixture.Operator.Requests);
    }

    [Fact]
    public async Task EachMerchantIssuesOnItsOwnNodeUnderItsOwnKeys()
    {
        using var fixture = new Fixture();
        var request = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" };

        var mine = await fixture.Create(request, "attempt-1");
        var theirs = await fixture.Service.CreateAsync(fixture.OtherIssuer, request, "attempt-1", default);

        Assert.Equal(InvoiceCreationOutcome.Created, mine.Outcome);
        Assert.Equal(InvoiceCreationOutcome.Created, theirs.Outcome);
        Assert.Equal(fixture.OtherBackend.Id, theirs.Invoice!.BackendId);
        Assert.Contains($"externalId=m{fixture.OtherMerchantId}%3A", Assert.Single(fixture.Other.Requests).Body);
        Assert.Single(fixture.Operator.Requests);
        Assert.Equal(theirs.Invoice.Bolt11, fixture.Other[theirs.Invoice.PaymentHash!].Bolt11);
    }

    [Fact]
    public async Task ARequestStillInFlightIsNotIssuedTwice()
    {
        using var fixture = new Fixture();
        var request = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" };
        fixture.Reserve("attempt-1", request, createdAt: Now.AddSeconds(-30));

        var result = await fixture.Create(request, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.IdempotencyInProgress, result.Outcome);
        Assert.Empty(fixture.Operator.Requests);
    }

    [Fact]
    public async Task AnAbandonedReservationIsReclaimed()
    {
        using var fixture = new Fixture();
        var request = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" };
        fixture.Reserve("attempt-1", request, createdAt: Now - LightningInvoiceService.AbandonedReservationAge - TimeSpan.FromSeconds(1));

        var result = await fixture.Create(request, "attempt-1");

        Assert.Equal(InvoiceCreationOutcome.Created, result.Outcome);
        var invoice = Assert.Single(fixture.Db.Invoices());
        Assert.Equal(LightningInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(Now, invoice.CreatedAtUtc);
        Assert.Single(fixture.Operator.Requests);
    }

    [Theory]
    [InlineData(false, InvoiceCreationOutcome.IdempotencyInProgress)]
    [InlineData(true, InvoiceCreationOutcome.Replayed)]
    public async Task ARequestThatLosesTheRaceForItsKeyIsAnsweredFromTheWinner(bool winnerFinished, InvoiceCreationOutcome expected)
    {
        var request = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" };
        // The winner reserves the key while this request is being priced, after its own check found the key free.
        using var fixture = new Fixture(duringQuote: f => f.Reserve("attempt-1", request, Now,
            winnerFinished ? LightningInvoiceStatus.Pending : LightningInvoiceStatus.Creating));

        var result = await fixture.Create(request, "attempt-1");

        Assert.Equal(expected, result.Outcome);
        Assert.Single(fixture.Db.Invoices());
        Assert.Empty(fixture.Operator.Requests);
    }

    [Fact]
    public async Task AReadRecordsThePaymentTheNodeProves()
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();
        fixture.Operator.Pay(hash);

        var read = await fixture.Service.GetAsync(fixture.MerchantId, hash.ToUpperInvariant(), default);

        Assert.Equal(InvoiceReadOutcome.Found, read.Outcome);
        Assert.Equal(LightningInvoiceStatus.Paid, read.Invoice!.Status);
        var stored = Assert.Single(fixture.Db.Invoices());
        Assert.Equal(LightningInvoiceStatus.Paid, stored.Status);
        Assert.Equal(10_000, stored.ReceivedSat);
        Assert.Equal(21_000, stored.FeeSat);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791853300000), stored.PaidAtUtc);
        Assert.Equal($"/payments/incoming/{hash}", fixture.Operator.Requests[^1].Path);

        // Paid is final, so later reads are answered from the record.
        var again = await fixture.Service.GetAsync(fixture.MerchantId, hash, default);

        Assert.Equal(LightningInvoiceStatus.Paid, again.Invoice!.Status);
        Assert.Equal(2, fixture.Operator.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-hex")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task APaymentTheNodeCannotProveIsNotRecorded(string? preimage)
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();
        fixture.Operator.Override = path => path.Contains("/payments/incoming/", StringComparison.Ordinal)
            ? FakePhoenixd.Json(new
            {
                paymentHash = hash, preimage, isPaid = true, isExpired = false, requestedSat = 10_000, receivedSat = 10_000,
                fees = 0, completedAt = 1791853300000, createdAt = 1791853243502,
            })
            : null;

        var read = await fixture.Service.GetAsync(fixture.MerchantId, hash, default);

        Assert.Equal(InvoiceReadOutcome.BackendFailure, read.Outcome);
        Assert.Equal("The node reported a payment it could not prove.", read.Message);
        Assert.Equal(LightningInvoiceStatus.Pending, Assert.Single(fixture.Db.Invoices()).Status);
    }

    [Fact]
    public async Task AnExpiredInvoiceIsRecordedAsExpiredAndNotAskedAgain()
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();
        fixture.Operator.Expire(hash);

        var read = await fixture.Service.GetAsync(fixture.MerchantId, hash, default);
        await fixture.Service.GetAsync(fixture.MerchantId, hash, default);

        Assert.Equal(LightningInvoiceStatus.Expired, read.Invoice!.Status);
        Assert.Equal(LightningInvoiceStatus.Expired, Assert.Single(fixture.Db.Invoices()).Status);
        Assert.Null(read.Invoice.PaidAtUtc);
        Assert.Equal(2, fixture.Operator.Requests.Count);
    }

    [Fact]
    public async Task APendingInvoiceIsAskedAboutOnEveryRead()
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();

        await fixture.Service.GetAsync(fixture.MerchantId, hash, default);
        var read = await fixture.Service.GetAsync(fixture.MerchantId, hash, default);

        Assert.Equal(LightningInvoiceStatus.Pending, read.Invoice!.Status);
        Assert.Equal(3, fixture.Operator.Requests.Count);
    }

    [Fact]
    public async Task AnInvoiceIsReadFromTheBackendThatIssuedItAfterTheMerchantMoves()
    {
        using var fixture = new Fixture();
        var before = await fixture.IssueAsync();
        var own = fixture.Network.Add("https://artcana.node/", new FakePhoenixd("artcana-limited"));
        fixture.MoveArtcanaTo("https://artcana.node/", "artcana-limited", own.NodeId);

        fixture.Operator.Pay(before);
        var read = await fixture.Service.GetAsync(fixture.MerchantId, before, default);
        var after = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" });

        Assert.Equal(LightningInvoiceStatus.Paid, read.Invoice!.Status);
        Assert.Equal($"/payments/incoming/{before}", fixture.Operator.Requests[^1].Path);
        Assert.Equal(InvoiceCreationOutcome.Created, after.Outcome);
        Assert.NotEqual(fixture.Backend.Id, after.Invoice!.BackendId);
        Assert.Equal("/createinvoice", Assert.Single(own.Requests).Path);
    }

    [Fact]
    public async Task AnotherMerchantsInvoiceLooksExactlyLikeAMissingOne()
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();

        var theirs = await fixture.Service.GetAsync(fixture.OtherMerchantId, hash, default);
        var unknown = await fixture.Service.GetAsync(fixture.MerchantId, new string('f', 64), default);

        Assert.Equal(InvoiceReadOutcome.NotFound, theirs.Outcome);
        Assert.Equal(InvoiceReadOutcome.NotFound, unknown.Outcome);
        Assert.Null(theirs.Invoice);
        Assert.Single(fixture.Operator.Requests); // only the creation; neither read reached a node
        Assert.Empty(fixture.Other.Requests);
    }

    [Fact]
    public async Task APendingInvoiceTheNodeNoLongerKnowsKeepsItsRecord()
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();
        fixture.Operator.Override = _ => FakePhoenixd.Text(HttpStatusCode.NotFound, "Not found");

        var read = await fixture.Service.GetAsync(fixture.MerchantId, hash, default);

        Assert.Equal(InvoiceReadOutcome.Found, read.Outcome);
        Assert.Equal(LightningInvoiceStatus.Pending, read.Invoice!.Status);
    }

    [Fact]
    public async Task NodeErrorsOnReadAreReported()
    {
        using var fixture = new Fixture();
        var hash = await fixture.IssueAsync();
        fixture.Operator.Override = _ => FakePhoenixd.Text(HttpStatusCode.InternalServerError, "boom");

        var read = await fixture.Service.GetAsync(fixture.MerchantId, hash, default);

        Assert.Equal(InvoiceReadOutcome.BackendFailure, read.Outcome);
        Assert.Equal(LightningInvoiceStatus.Pending, Assert.Single(fixture.Db.Invoices()).Status);
    }

    [Fact]
    public async Task WithoutTheOperatorsNodeItsMerchantsCannotIssueOrRefreshButSettledInvoicesStillRead()
    {
        using var fixture = new Fixture(operatorConfigured: false);
        var pending = fixture.Seed(fixture.MerchantId, LightningInvoiceStatus.Pending);
        var paid = fixture.Seed(fixture.MerchantId, LightningInvoiceStatus.Paid);

        var create = await fixture.Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" });
        var readPending = await fixture.Service.GetAsync(fixture.MerchantId, pending.PaymentHash!, default);
        var readPaid = await fixture.Service.GetAsync(fixture.MerchantId, paid.PaymentHash!, default);
        var theirs = await fixture.Service.CreateAsync(fixture.OtherIssuer,
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, null, default);

        Assert.Equal(InvoiceCreationOutcome.BackendUnavailable, create.Outcome);
        Assert.Contains("PhoenixConfig:Host", create.Message);
        Assert.Equal(InvoiceReadOutcome.BackendUnavailable, readPending.Outcome);
        Assert.Equal(InvoiceReadOutcome.Found, readPaid.Outcome);
        Assert.Equal(InvoiceCreationOutcome.Created, theirs.Outcome); // the other merchant has its own node
    }

    [Fact]
    public async Task AStoredPasswordThatCannotBeDecryptedMakesTheBackendUnavailable()
    {
        using var fixture = new Fixture();
        fixture.OtherBackend.ProtectedPassword = TestCredentials.Protector().Protect(fixture.OtherMerchantId, "other-limited");
        fixture.Update(fixture.OtherBackend); // encrypted under a key this server does not have

        var result = await fixture.Service.CreateAsync(fixture.OtherIssuer,
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, null, default);
        var status = await fixture.Service.GetStatusAsync(fixture.OtherMerchantId, default);

        Assert.Equal(InvoiceCreationOutcome.BackendUnavailable, result.Outcome);
        Assert.Contains("cannot be decrypted", result.Message);
        Assert.True(status.Enabled);
        Assert.False(status.Reachable);
        Assert.Empty(fixture.Other.Requests);
    }

    [Fact]
    public async Task StatusReportsWhetherTheMerchantsPinnedNodeAnswers()
    {
        using var fixture = new Fixture();
        var newcomer = fixture.Db.AddMerchant("Newcomer").Id;

        var healthy = await fixture.Service.GetStatusAsync(fixture.MerchantId, default);
        var none = await fixture.Service.GetStatusAsync(newcomer, default);
        fixture.Operator.NodeKey = Bolt11TestInvoices.NewKey();
        var replaced = await fixture.Service.GetStatusAsync(fixture.MerchantId, default);
        fixture.Operator.Override = _ => FakePhoenixd.Text(HttpStatusCode.ServiceUnavailable, "starting");
        var down = await fixture.Service.GetStatusAsync(fixture.MerchantId, default);

        Assert.True(healthy is { Enabled: true, Reachable: true });
        Assert.True(none is { Enabled: false, Reachable: false });
        Assert.True(replaced is { Enabled: true, Reachable: false });
        Assert.True(down is { Enabled: true, Reachable: false });
        Assert.All(fixture.Operator.Requests, r => Assert.Equal("/getinfo", r.Path));
    }

    [Fact]
    public async Task ListingPagesTheMerchantsInvoicesNewestFirst()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(1, 5)
            .Select(i => fixture.Seed(fixture.MerchantId, i % 2 == 0 ? LightningInvoiceStatus.Paid : LightningInvoiceStatus.Pending, $"order-{i}").Id)
            .ToList();
        fixture.Seed(fixture.MerchantId, LightningInvoiceStatus.Creating, "order-6");
        fixture.Seed(fixture.OtherMerchantId, LightningInvoiceStatus.Pending, "order-1");

        var first = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(Limit: 2), default);
        var second = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(Limit: 2, Before: first.NextCursor), default);
        var last = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(Limit: 2, Before: second.NextCursor), default);

        Assert.Equal(new[] { ids[4], ids[3] }, first.Items.Select(i => i.Id).ToArray());
        Assert.Equal(ids[3], first.NextCursor);
        Assert.Equal(new[] { ids[2], ids[1] }, second.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { ids[0] }, last.Items.Select(i => i.Id).ToArray());
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task ListingFiltersByStatusAndReference()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(1, 4)
            .Select(i => fixture.Seed(fixture.MerchantId, i % 2 == 0 ? LightningInvoiceStatus.Paid : LightningInvoiceStatus.Pending, $"order-{i}").Id)
            .ToList();
        fixture.Seed(fixture.MerchantId, LightningInvoiceStatus.Creating, "order-1");
        fixture.Seed(fixture.OtherMerchantId, LightningInvoiceStatus.Paid, "order-1");

        var paid = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(LightningInvoiceStatus.Paid), default);
        var byReference = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(ExternalId: "order-1"), default);
        var everything = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(), default);

        Assert.Equal(new[] { ids[3], ids[1] }, paid.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { ids[0] }, byReference.Items.Select(i => i.Id).ToArray());
        Assert.Equal(4, everything.Items.Count); // the unfinished reservation is never listed
        Assert.Null(everything.NextCursor);
    }

    [Fact]
    public async Task PageSizesAreClamped()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < LightningInvoiceService.DefaultPageSize + 1; i++)
            fixture.Seed(fixture.MerchantId, LightningInvoiceStatus.Pending);

        var byDefault = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(), default);
        var atLeastOne = await fixture.Service.ListAsync(fixture.MerchantId, new LightningInvoiceQuery(Limit: 0), default);

        Assert.Equal(LightningInvoiceService.DefaultPageSize, byDefault.Items.Count);
        Assert.NotNull(byDefault.NextCursor);
        Assert.Single(atLeastOne.Items);
    }

    [Fact]
    public void RetriesAreRecognisedHoweverTheirJsonSpellsTheRequest()
    {
        var original = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", ExternalId = "order-123", Description = "Pedido 123" };
        var respelled = new CreateLightningInvoiceRequest { Amount = 40.000m, Currency = " brl", ExternalId = "order-123", Description = "Pedido 123 " };
        var otherOrder = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", ExternalId = "order-124", Description = "Pedido 123" };
        var otherLifetime = new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", ExternalId = "order-123", Description = "Pedido 123", ExpirySeconds = 600 };

        Assert.Equal(LightningInvoiceService.HashRequest(original), LightningInvoiceService.HashRequest(respelled));
        Assert.NotEqual(LightningInvoiceService.HashRequest(original), LightningInvoiceService.HashRequest(otherOrder));
        Assert.NotEqual(LightningInvoiceService.HashRequest(original), LightningInvoiceService.HashRequest(otherLifetime));
    }

    private static HttpResponseMessage Tampered(FakePhoenixd node, string tamper)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var otherHash = new string('a', 64);
        var bolt11 = tamper switch
        {
            "amount" => Bolt11TestInvoices.Create(node.NodeKey, hash, 9_999_000, "Payment of 40 BRL", 600, node.Now),
            "hash" => Bolt11TestInvoices.Create(node.NodeKey, otherHash, 10_000_000, "Payment of 40 BRL", 600, node.Now),
            "expiry" => Bolt11TestInvoices.Create(node.NodeKey, hash, 10_000_000, "Payment of 40 BRL", 3600, node.Now),
            _ => "lnbc1notaninvoice",
        };
        return FakePhoenixd.Json(new { amountSat = 10_000, paymentHash = hash, serialized = bolt11 });
    }

    private static BitcoinExchangeRateModel Rates(int sourceCount = 3) => new()
    {
        BitcoinRateInUSD = 80_000m,
        SourceCount = sourceCount,
        CurrencyExchangeRate = new CurrencyExchangeRateModel
        {
            Date = Now.AddHours(-2).UtcDateTime,
            Rates = [new() { Currency = "USD", Rate = 1m }, new() { Currency = "BRL", Rate = 5m }]
        }
    };

    private sealed class Fixture : IDisposable
    {
        private const string OperatorUrl = "https://operator.node/";
        private const string OtherUrl = "https://other.node/";

        private readonly AesGcmCredentialProtector _protector = TestCredentials.Protector();
        private int _seeded;

        public Fixture(BitcoinExchangeRateModel? rates = null, LightningInvoiceOptions? settings = null,
            bool operatorConfigured = true, bool pinned = true, Action<Fixture>? duringQuote = null)
        {
            Operator = Network.Add(OperatorUrl, new FakePhoenixd("operator-limited"));
            Other = Network.Add(OtherUrl, new FakePhoenixd("other-limited"));

            MerchantId = Db.AddMerchant("Artcana").Id;
            OtherMerchantId = Db.AddMerchant("Other").Id;
            KeyId = Db.AddKey(MerchantId).Id;
            Backend = Db.Add(new LightningBackend
            {
                MerchantId = MerchantId, Kind = LightningBackendKind.OperatorNode, IsActive = true, CreatedAtUtc = Now,
                NodeId = pinned ? Operator.NodeId : null, Chain = pinned ? "mainnet" : null,
            });
            OtherBackend = Db.Add(new LightningBackend
            {
                MerchantId = OtherMerchantId, Kind = LightningBackendKind.Phoenixd, Url = OtherUrl,
                ProtectedPassword = _protector.Protect(OtherMerchantId, "other-limited"),
                IsActive = true, CreatedAtUtc = Now, NodeId = Other.NodeId, Chain = "mainnet",
            });

            var factory = new LightningBackendFactory(Network, _protector, operatorConfigured
                ? new PhoenixConfig { Host = OperatorUrl, Token = "operator-full", LimitedAccessToken = "operator-limited" }
                : null);

            ICurrencyConversionService conversion = new CurrencyConversionService(new StubRates(rates ?? Rates()), Clock);
            if (duringQuote is not null)
                conversion = new RacingConversion(conversion, () => duringQuote(this));

            Service = new LightningInvoiceService(conversion, Db.Repository<LightningInvoice>(), Db.Repository<LightningBackend>(),
                factory, Microsoft.Extensions.Options.Options.Create(settings ?? new LightningInvoiceOptions()),
                Clock, NullLogger<LightningInvoiceService>.Instance);
        }

        public TestDatabase Db { get; } = new();
        public ManualClock Clock { get; } = new(Now);
        public FakeLightningNetwork Network { get; } = new();
        public FakePhoenixd Operator { get; }
        public FakePhoenixd Other { get; }
        public LightningBackend Backend { get; }
        public LightningBackend OtherBackend { get; }
        public LightningInvoiceService Service { get; }
        public int MerchantId { get; }
        public int OtherMerchantId { get; }
        public int KeyId { get; }
        public InvoiceIssuer Issuer => new(MerchantId, KeyId);
        public InvoiceIssuer OtherIssuer => new(OtherMerchantId, null);

        public Task<InvoiceCreationResult> Create(CreateLightningInvoiceRequest request, string? idempotencyKey = null) =>
            Service.CreateAsync(Issuer, request, idempotencyKey, default);

        /// <summary>Issues an R$ 40 invoice for Artcana and returns its payment hash.</summary>
        public async Task<string> IssueAsync()
        {
            var result = await Create(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", ExternalId = "order-123" });
            return result.Invoice?.PaymentHash ?? throw new InvalidOperationException($"Not issued: {result.Outcome} {result.Message}");
        }

        /// <summary>Retires Artcana's backend for its own phoenixd, as an operator reconfiguring it would.</summary>
        public void MoveArtcanaTo(string url, string password, string nodeId)
        {
            using (var db = Db.CreateDbContext())
            {
                var current = db.LightningBackends.Single(b => b.Id == Backend.Id);
                current.IsActive = false;
                current.RetiredAtUtc = Now;
                db.SaveChanges();
            }

            Db.Add(new LightningBackend
            {
                MerchantId = MerchantId, Kind = LightningBackendKind.Phoenixd, Url = url,
                ProtectedPassword = _protector.Protect(MerchantId, password),
                IsActive = true, CreatedAtUtc = Now, NodeId = nodeId, Chain = "mainnet",
            });
        }

        public void Update(LightningBackend backend)
        {
            using var db = Db.CreateDbContext();
            db.LightningBackends.Update(backend);
            db.SaveChanges();
        }

        public LightningBackend StoredBackend(int id)
        {
            using var db = Db.CreateDbContext();
            return db.LightningBackends.Single(b => b.Id == id);
        }

        /// <summary>Another request holding <paramref name="idempotencyKey"/> for the same request.</summary>
        public LightningInvoice Reserve(string idempotencyKey, CreateLightningInvoiceRequest request, DateTimeOffset createdAt,
            LightningInvoiceStatus status = LightningInvoiceStatus.Creating)
        {
            var invoice = NewInvoice(MerchantId, status, request.ExternalId);
            invoice.IdempotencyKey = idempotencyKey;
            invoice.RequestHash = LightningInvoiceService.HashRequest(request);
            invoice.CreatedAtUtc = createdAt;
            return Db.Add(invoice);
        }

        public LightningInvoice Seed(int merchantId, LightningInvoiceStatus status, string? externalId = null) =>
            Db.Add(NewInvoice(merchantId, status, externalId));

        public void Dispose()
        {
            Network.Dispose();
            Db.Dispose();
        }

        private LightningInvoice NewInvoice(int merchantId, LightningInvoiceStatus status, string? externalId)
        {
            var n = ++_seeded;
            var issued = status != LightningInvoiceStatus.Creating;
            return new LightningInvoice
            {
                MerchantId = merchantId,
                BackendId = merchantId == MerchantId ? Backend.Id : OtherBackend.Id,
                Status = status,
                PaymentHash = issued ? n.ToString("x64") : null,
                Bolt11 = issued ? $"lnbc-seeded-{n}" : null,
                ExternalId = externalId,
                Description = "Payment of 40 BRL",
                Amount = 40m,
                Currency = "BRL",
                AmountSat = 10_000,
                BitcoinPrice = 400_000m,
                BitcoinPriceUsd = 80_000m,
                RateSources = 3,
                QuotedAtUtc = Now,
                ExpiresAtUtc = Now.AddMinutes(10),
                CreatedAtUtc = Now,
            };
        }
    }

    /// <summary>Lets another request act while this one is being priced: after its key check, before its reservation.</summary>
    private sealed class RacingConversion(ICurrencyConversionService inner, Action race) : ICurrencyConversionService
    {
        private bool _raced;

        public IReadOnlyCollection<string> SupportedCurrencies => inner.SupportedCurrencies;

        public Task<ConversionResponse> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken) =>
            inner.ConvertAsync(request, cancellationToken);

        public Task<SatsQuote> QuoteSatsAsync(decimal amount, string currency, SatsQuoteRules rules, CancellationToken cancellationToken)
        {
            if (!_raced)
            {
                _raced = true;
                race();
            }

            return inner.QuoteSatsAsync(amount, currency, rules, cancellationToken);
        }
    }

    private sealed class StubRates(BitcoinExchangeRateModel rates) : IExchangeRateService
    {
        public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; } = [];

        public Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BitcoinExchangeRateModel?>(rates);
    }
}
