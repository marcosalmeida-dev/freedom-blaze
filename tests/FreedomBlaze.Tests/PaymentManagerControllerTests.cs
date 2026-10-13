using System.Net;
using System.Text;
using FreedomBlaze.Authentication;
using FreedomBlaze.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenixd.NET;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Tests;

public sealed class PaymentManagerControllerTests
{
    [Fact]
    public void PaymentOperationsRequireMasterApiKey()
    {
        var authorization = Assert.Single(typeof(PaymentManagerController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());
        Assert.Equal(ApiKeyDefaults.Scheme, authorization.AuthenticationSchemes);
        Assert.Equal(ApiKeyDefaults.MasterPolicy, authorization.Policy);
        Assert.All(typeof(PaymentManagerController).GetMethods()
            .Where(method => method.ReturnType == typeof(Task<IActionResult>)), method =>
                Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)));
    }

    [Fact]
    public async Task AllOperationsReturnUnavailableWhenPhoenixdIsDisabled()
    {
        var controller = CreateController();
        var actions = typeof(PaymentManagerController).GetMethods()
            .Where(method => method.ReturnType == typeof(Task<IActionResult>));
        foreach (var action in actions)
        {
            var arguments = action.GetParameters().Select(parameter =>
                parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
            var result = await (Task<IActionResult>)action.Invoke(controller, arguments)!;
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
        }
    }

    [Fact]
    public async Task SendAllInvoiceUsesPhoenixdFlagWithoutAmount()
    {
        using var fixture = new PaymentFixture(_ => Json("""
            {"type":"payment_sent","recipientAmountSat":1000,"routingFeeSat":2,
             "paymentId":"79b89b6b-ce86-4f89-a22c-b00d325c3754","paymentHash":"hash","paymentPreimage":"preimage"}
            """));

        var result = await fixture.Controller.SendLightningInvoice(0, "lnbc-invoice", default, sendAll: true);

        Assert.IsType<PayInvoiceResponse>(Assert.IsType<OkObjectResult>(result).Value);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("/payinvoice", request.Path);
        Assert.Contains("sendAll=", request.Body);
        Assert.DoesNotContain("amountSat", request.Body);
    }

    [Fact]
    public async Task SendAllWithExplicitAmountIsRejectedBeforeContactingDaemon()
    {
        using var fixture = new PaymentFixture(_ => Json("{}"));

        var result = await fixture.Controller.SendLightningInvoice(100, "lnbc-invoice", default, sendAll: true);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task AmountlessInvoiceOmitsAmountAndKeepsWebhookMetadata()
    {
        using var fixture = new PaymentFixture(_ => Json("""
            {"paymentHash":"hash","serialized":"lnbc-amountless"}
            """));

        var result = await fixture.Controller.CreateInvoice(new CreateInvoiceRequest
        {
            Description = "Donation", ExternalId = "donation-42", WebhookUrl = "https://example.com/paid"
        }, default);

        var invoice = Assert.IsType<Invoice>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(invoice.AmountSat);
        var request = Assert.Single(fixture.Requests);
        Assert.DoesNotContain("amountSat", request.Body);
        Assert.Contains("externalId=donation-42", request.Body);
        Assert.Contains("webhookUrl=", request.Body);
    }

    [Fact]
    public async Task SwapInAddressAndBalanceExposeElectrumWalletData()
    {
        using var fixture = new PaymentFixture(path => Json(path == "/getswapinaddress" ? """
            {"address":"bc1p-swapin","index":5}
            """ : """
            {"balanceSat":1000,"feeCreditSat":10,"swapIn":{"unconfirmedBalanceSat":200,
             "weaklyConfirmedBalanceSat":300,"deeplyConfirmedBalanceSat":400}}
            """));

        var address = Assert.IsType<SwapInAddress>(Assert.IsType<OkObjectResult>(
            await fixture.Controller.GetSwapInAddress(default)).Value);
        var balance = Assert.IsType<Balance>(Assert.IsType<OkObjectResult>(
            await fixture.Controller.GetBalance(default)).Value);

        Assert.Equal(5, address.Index);
        Assert.Equal("bc1p-swapin", address.Address);
        Assert.Equal(200, balance.SwapIn!.UnconfirmedBalanceSat);
        Assert.Equal(300, balance.SwapIn.WeaklyConfirmedBalanceSat);
        Assert.Equal(400, balance.SwapIn.DeeplyConfirmedBalanceSat);
    }

    [Fact]
    public async Task OnchainOutgoingPaymentIsRetrievedByUuid()
    {
        const string paymentId = "79b89b6b-ce86-4f89-a22c-b00d325c3754";
        using var fixture = new PaymentFixture(_ => Json("""
            {"type":"outgoing_payment","subType":"splice_out",
             "paymentId":"79b89b6b-ce86-4f89-a22c-b00d325c3754",
             "txId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "isPaid":true,"sent":1000,"fees":2000,"createdAt":1,"completedAt":2}
            """));

        var result = await fixture.Controller.GetOutgoingPayment(paymentId, default);

        var payment = Assert.IsType<PaymentInfoOutgoing>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("splice_out", payment.SubType);
        Assert.Equal(paymentId, payment.PaymentId);
        Assert.Equal(64, payment.TxId!.Length);
        Assert.Equal($"/payments/outgoing/{paymentId}", Assert.Single(fixture.Requests).Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task MissingPaymentReturnsNotFound(HttpStatusCode status)
    {
        using var fixture = new PaymentFixture(_ => new HttpResponseMessage(status));

        var result = await fixture.Controller.GetIncomingPayment(new string('a', 64), default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Http200PaymentFailureDoesNotReportSuccessfulPayment()
    {
        using var fixture = new PaymentFixture(_ => Json("""
            {"type":"payment_failed","paymentHash":"hash","reason":"insufficient balance"}
            """));

        var result = await fixture.Controller.SendLightningInvoice(100, "lnbc-invoice", default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Http200ChannelCloseFailureReturnsProviderError()
    {
        using var fixture = new PaymentFixture(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("no channel available", Encoding.UTF8, "text/plain")
        });

        var result = await fixture.Controller.CloseChannel(new string('a', 64), "bc1q-address", 5, default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("/closechannel", Assert.Single(fixture.Requests).Path);
    }

    private static PaymentManagerController CreateController(IPaymentService? payments = null, INodeService? node = null) =>
        new(NullLogger<PaymentManagerController>.Instance, payments, node)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class PaymentFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public List<(string Path, string Body)> Requests { get; } = [];
        public PaymentManagerController Controller { get; }

        public PaymentFixture(Func<string, HttpResponseMessage> respond)
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(
                () => new PaymentHandler(Requests, respond)));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PhoenixConfig:Host"] = "https://phoenixd.example",
                ["PhoenixConfig:Token"] = "test-password"
            }).Build();
            services.ConfigurePhoenixdServices(configuration);
            _provider = services.BuildServiceProvider();
            Controller = CreateController(_provider.GetRequiredService<IPaymentService>(),
                _provider.GetRequiredService<INodeService>());
        }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class PaymentHandler(
        List<(string Path, string Body)> requests, Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(path);
        }
    }
}
