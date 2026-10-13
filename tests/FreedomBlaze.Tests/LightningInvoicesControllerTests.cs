using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Text;
using FreedomBlaze.Authentication;
using FreedomBlaze.Controllers;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenixd.NET;
using Phoenixd.NET.Interfaces;

namespace FreedomBlaze.Tests;

/// <summary>
/// Drives the controller against the real phoenixd.NET client with a stubbed HTTP handler. Rates are
/// 80,000 USD per bitcoin and 5 BRL per dollar, so R$ 40 is exactly 10,000 sats.
/// </summary>
public sealed class LightningInvoicesControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void AnyActiveKeyMayIssueInvoicesAndEachKeyIsThrottled()
    {
        var authorization = Assert.Single(typeof(LightningInvoicesController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());
        Assert.Equal(ApiKeyDefaults.Scheme, authorization.AuthenticationSchemes);
        Assert.Null(authorization.Policy);

        var throttle = Assert.Single(typeof(LightningInvoicesController)
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true).Cast<EnableRateLimitingAttribute>());
        Assert.Equal(RateLimitPolicies.PerApiKey, throttle.PolicyName);
    }

    [Fact]
    public async Task CreatesAnInvoiceForTheQuotedSatsTaggedWithTheCallingKey()
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"amountSat":10000,"paymentHash":"{{Hash}}","serialized":"lnbc100u1-test"}
            """));

        var result = await fixture.Controller.CreateInvoice(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "brl", ExternalId = "order-123", Description = "Artcana pedido 123"
        }, default);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(Hash, created.RouteValues!["paymentHash"]);
        var invoice = Assert.IsType<LightningInvoiceResponse>(created.Value);
        Assert.Equal(Hash, invoice.PaymentHash);
        Assert.Equal("lnbc100u1-test", invoice.Bolt11);
        Assert.Equal(10_000, invoice.AmountSat);
        Assert.Equal(40m, invoice.Amount);
        Assert.Equal("BRL", invoice.Currency);
        Assert.Equal(400_000m, invoice.BitcoinPrice);
        Assert.Equal(3, invoice.RateSources);
        Assert.Equal(Now, invoice.QuotedAt);
        Assert.Equal(Now.AddSeconds(600), invoice.ExpiresAt);
        Assert.Equal("order-123", invoice.ExternalId);

        var request = Assert.Single(fixture.Requests);
        Assert.Equal("/createinvoice", request.Path);
        Assert.Contains("amountSat=10000", request.Body);
        Assert.Contains("externalId=key7%3Aorder-123", request.Body);
        Assert.Contains("expirySeconds=600", request.Body);
        Assert.Contains("description=Artcana+pedido+123", request.Body);
        Assert.DoesNotContain("webhookUrl", request.Body);
    }

    [Fact]
    public async Task InvoiceWithoutReferenceIsStillTaggedAndDescribedByItsAmount()
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"amountSat":2501,"paymentHash":"{{Hash}}","serialized":"lnbc-test"}
            """));

        var result = await fixture.Controller.CreateInvoice(new CreateLightningInvoiceRequest
        {
            Amount = 10.0001m, Currency = "BRL", ExpirySeconds = 120
        }, default);

        var invoice = Assert.IsType<LightningInvoiceResponse>(Assert.IsType<CreatedAtActionResult>(result).Value);
        Assert.Equal(2_501, invoice.AmountSat); // rounded up, never down
        Assert.Null(invoice.ExternalId);
        Assert.Equal(Now.AddSeconds(120), invoice.ExpiresAt);
        var body = Assert.Single(fixture.Requests).Body;
        Assert.Contains("externalId=key7%3A&", body);
        Assert.Contains("description=Payment+of+10.0001+BRL", body);
        Assert.Contains("expirySeconds=120", body);
    }

    [Theory]
    [InlineData("http://artcana.com.br/api/payments/lightning/webhook")]
    [InlineData("https://user:secret@artcana.com.br/hook")]
    [InlineData("/relative/hook")]
    public async Task UnsafeWebhooksAreRejectedBeforeContactingTheNode(string webhookUrl)
    {
        using var fixture = new Fixture(_ => Json("{}"));

        var result = await fixture.Controller.CreateInvoice(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "BRL", WebhookUrl = webhookUrl
        }, default);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task WebhookHostsAreLimitedToTheAllowList()
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"amountSat":10000,"paymentHash":"{{Hash}}","serialized":"lnbc-test"}
            """), settings: new LightningInvoiceOptions { AllowedWebhookHosts = ["artcana.com.br"] });

        var refused = await fixture.Controller.CreateInvoice(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "BRL", WebhookUrl = "https://evil.example/hook"
        }, default);
        var accepted = await fixture.Controller.CreateInvoice(new CreateLightningInvoiceRequest
        {
            Amount = 40m, Currency = "BRL", WebhookUrl = "https://ARTCANA.com.br/api/payments/lightning/webhook"
        }, default);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(refused).StatusCode);
        Assert.IsType<CreatedAtActionResult>(accepted);
        Assert.Contains("webhookUrl=https%3A%2F%2FARTCANA.com.br", Assert.Single(fixture.Requests).Body);
    }

    [Fact]
    public async Task AmountsAboveTheCeilingAreRejected()
    {
        using var fixture = new Fixture(_ => Json("{}"), settings: new LightningInvoiceOptions { MaxAmountSat = 5_000 });

        var result = await fixture.Controller.CreateInvoice(
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, default);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(0, "BRL")]
    [InlineData(10, "XYZ")]
    public async Task BadAmountsAndCurrenciesAreRejected(double amount, string currency)
    {
        using var fixture = new Fixture(_ => Json("{}"));

        var result = await fixture.Controller.CreateInvoice(
            new CreateLightningInvoiceRequest { Amount = (decimal)amount, Currency = currency }, default);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task UntrustworthyPricesAreNotInvoiced()
    {
        using var fixture = new Fixture(_ => Json("{}"), rates: Rates(sourceCount: 1));

        var result = await fixture.Controller.CreateInvoice(
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task NodeFailuresAreReportedAsBadGateway()
    {
        using var fixture = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });

        var result = await fixture.Controller.CreateInvoice(
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task EverythingIsUnavailableWithoutANode()
    {
        var controller = CreateController(payments: null, node: null, Rates(), new LightningInvoiceOptions());

        var create = await controller.CreateInvoice(new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL" }, default);
        var read = await controller.GetInvoice(Hash, default);
        var status = await controller.GetStatus(default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(create).StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(read).StatusCode);
        var gateway = Assert.IsType<LightningGatewayStatus>(Assert.IsType<OkObjectResult>(status.Result).Value);
        Assert.False(gateway.Enabled);
        Assert.False(gateway.Reachable);
    }

    [Fact]
    public async Task IssuerSeesAPaidInvoice()
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"type":"incoming_payment","subType":"lightning","paymentHash":"{{Hash}}","externalId":"key7:order-123",
             "invoice":"lnbc100u1-test","isPaid":true,"isExpired":false,"requestedSat":10000,"receivedSat":10000,
             "fees":21000000,"expiresAt":1791853363502,"completedAt":1791853300000,"createdAt":1791853243502}
            """));

        var result = await fixture.Controller.GetInvoice(Hash.ToUpperInvariant(), default);

        var status = Assert.IsType<LightningInvoiceStatusResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(LightningInvoiceStatusResponse.Paid, status.Status);
        Assert.True(status.IsPaid);
        Assert.Equal("order-123", status.ExternalId);
        Assert.Equal(10_000, status.RequestedSat);
        Assert.Equal(10_000, status.ReceivedSat);
        Assert.Equal(21_000, status.FeeSat);
        Assert.Equal("lnbc100u1-test", status.Bolt11);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791853300000), status.CompletedAt);
        Assert.Equal($"/payments/incoming/{Hash}", Assert.Single(fixture.Requests).Path);
    }

    [Fact]
    public async Task ExpiredUnpaidInvoiceReportsExpired()
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"paymentHash":"{{Hash}}","externalId":"key7:order-123","isPaid":false,"isExpired":true,
             "requestedSat":10000,"receivedSat":0,"fees":0,"createdAt":1791853243502}
            """));

        var status = Assert.IsType<LightningInvoiceStatusResponse>(
            Assert.IsType<OkObjectResult>(await fixture.Controller.GetInvoice(Hash, default)).Value);

        Assert.Equal(LightningInvoiceStatusResponse.Expired, status.Status);
        Assert.False(status.IsPaid);
        Assert.Null(status.CompletedAt);
    }

    [Fact]
    public async Task PendingInvoiceReportsPending()
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"paymentHash":"{{Hash}}","externalId":"key7:","isPaid":false,"isExpired":false,
             "requestedSat":10000,"receivedSat":0,"fees":0,"createdAt":1791853243502}
            """));

        var status = Assert.IsType<LightningInvoiceStatusResponse>(
            Assert.IsType<OkObjectResult>(await fixture.Controller.GetInvoice(Hash, default)).Value);

        Assert.Equal(LightningInvoiceStatusResponse.Pending, status.Status);
        Assert.Null(status.ExternalId);
    }

    [Theory]
    [InlineData("\"externalId\":\"key8:order-123\",")]
    [InlineData("\"externalId\":\"key77:order-123\",")]
    [InlineData("")]
    public async Task AnotherKeysInvoiceLooksExactlyLikeAMissingOne(string externalId)
    {
        using var fixture = new Fixture(_ => Json($$"""
            {"paymentHash":"{{Hash}}",{{externalId}}"isPaid":true,"isExpired":false,"requestedSat":10000,
             "receivedSat":10000,"fees":0,"createdAt":1791853243502}
            """));

        Assert.IsType<NotFoundResult>(await fixture.Controller.GetInvoice(Hash, default));
    }

    [Fact]
    public async Task UnknownInvoiceIsNotFound()
    {
        using var fixture = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.IsType<NotFoundResult>(await fixture.Controller.GetInvoice(Hash, default));
    }

    [Fact]
    public async Task MalformedPaymentHashIsRejectedWithoutContactingTheNode()
    {
        using var fixture = new Fixture(_ => Json("{}"));

        var result = await fixture.Controller.GetInvoice("../getbalance", default);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task GatewayStatusProbesTheNode()
    {
        using var healthy = new Fixture(_ => Json("""
            {"nodeId":"03abc","channels":[],"chain":"mainnet","blockHeight":971166,"version":"0.9.2"}
            """));
        using var broken = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var up = Assert.IsType<LightningGatewayStatus>(
            Assert.IsType<OkObjectResult>((await healthy.Controller.GetStatus(default)).Result).Value);
        var down = Assert.IsType<LightningGatewayStatus>(
            Assert.IsType<OkObjectResult>((await broken.Controller.GetStatus(default)).Result).Value);

        Assert.True(up.Enabled);
        Assert.True(up.Reachable);
        Assert.Equal("/getinfo", Assert.Single(healthy.Requests).Path);
        Assert.True(down.Enabled);
        Assert.False(down.Reachable);
    }

    [Theory]
    [InlineData("order 123", null, null, false)]
    [InlineData("order/123", null, null, false)]
    [InlineData("artstore:1:123:45", null, null, true)]
    [InlineData(null, 59, null, false)]
    [InlineData(null, 3601, null, false)]
    [InlineData(null, 3600, null, true)]
    [InlineData(null, null, 129, false)]
    [InlineData(null, null, 128, true)]
    public void RequestValidationGuardsReferencesLifetimesAndDescriptions(
        string? externalId, int? expirySeconds, int? descriptionLength, bool valid)
    {
        var request = new CreateLightningInvoiceRequest
        {
            Amount = 40m,
            Currency = "BRL",
            ExternalId = externalId,
            ExpirySeconds = expirySeconds,
            Description = descriptionLength is { } length ? new string('x', length) : null,
        };

        Assert.Equal(valid, Validator.TryValidateObject(
            request, new ValidationContext(request), [], validateAllProperties: true));
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

    private static LightningInvoicesController CreateController(
        IPaymentService? payments, INodeService? node, BitcoinExchangeRateModel rates, LightningInvoiceOptions settings)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ApiKeyDefaults.KeyIdClaim, "7"), new Claim(ApiKeyDefaults.MasterClaim, "false")],
            ApiKeyDefaults.Scheme));

        return new LightningInvoicesController(
            new CurrencyConversionService(new StubRates(rates), new FixedClock(Now)),
            Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<LightningInvoicesController>.Instance,
            payments,
            node)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public List<(string Path, string Body)> Requests { get; } = [];
        public LightningInvoicesController Controller { get; }

        public Fixture(Func<string, HttpResponseMessage> respond,
            BitcoinExchangeRateModel? rates = null, LightningInvoiceOptions? settings = null)
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(
                () => new RecordingHandler(Requests, respond)));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PhoenixConfig:Host"] = "https://phoenixd.example",
                ["PhoenixConfig:Token"] = "test-password"
            }).Build();
            services.ConfigurePhoenixdServices(configuration);
            _provider = services.BuildServiceProvider();
            Controller = CreateController(_provider.GetRequiredService<IPaymentService>(),
                _provider.GetRequiredService<INodeService>(), rates ?? Rates(), settings ?? new LightningInvoiceOptions());
        }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class RecordingHandler(
        List<(string Path, string Body)> requests, Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(path);
        }
    }

    private sealed class StubRates(BitcoinExchangeRateModel rates) : IExchangeRateService
    {
        public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; } = [];

        public Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BitcoinExchangeRateModel?>(rates);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
