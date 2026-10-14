using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using FreedomBlaze.Authentication;
using FreedomBlaze.Controllers;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace FreedomBlaze.Tests;

/// <summary>
/// The HTTP surface over <see cref="ILightningInvoiceService"/>: scopes, the merchant behind the key,
/// the <c>Idempotency-Key</c> header and how each outcome is answered. The service's own behaviour is
/// covered by <see cref="LightningInvoiceServiceTests"/>.
/// </summary>
public sealed class LightningInvoicesControllerTests
{
    private const int MerchantId = 3;
    private const int KeyId = 7;
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    public static TheoryData<string> MalformedIdempotencyKeys =>
        new() { "", "has space", "tab\tkey", "pedido-ç", new string('k', 101) };

    public static TheoryData<string> WellFormedIdempotencyKeys =>
        new() { "3f2b8c1e-7a4d-4e5f-9b6a-0c1d2e3f4a5b", "artstore:order:3764:attempt:2", new string('k', 100) };

    [Fact]
    public void EachEndpointRequiresItsScopeAndEachKeyIsThrottled()
    {
        var type = typeof(LightningInvoicesController);
        var authorization = Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(ApiKeyDefaults.Scheme, authorization.AuthenticationSchemes);
        Assert.Null(authorization.Policy);

        Assert.Equal(ApiKeyPolicies.InvoicesWrite, PolicyOf(nameof(LightningInvoicesController.GetStatus)));
        Assert.Equal(ApiKeyPolicies.InvoicesWrite, PolicyOf(nameof(LightningInvoicesController.CreateInvoice)));
        Assert.Equal(ApiKeyPolicies.InvoicesRead, PolicyOf(nameof(LightningInvoicesController.GetInvoice)));
        Assert.Equal(ApiKeyPolicies.InvoicesRead, PolicyOf(nameof(LightningInvoicesController.ListInvoices)));

        var throttle = Assert.Single(type.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));
        Assert.Equal(RateLimitPolicies.PerApiKey, throttle.PolicyName);

        static string? PolicyOf(string action) =>
            Assert.Single(typeof(LightningInvoicesController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>()).Policy;
    }

    [Fact]
    public void TheConversionApiRequiresTheConvertScope()
    {
        var authorization = Assert.Single(typeof(ConversionApiController).GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        Assert.Equal(ApiKeyDefaults.Scheme, authorization.AuthenticationSchemes);
        Assert.Equal(ApiKeyPolicies.Convert, authorization.Policy);
    }

    [Fact]
    public async Task KeysWithoutAMerchantCannotIssueOrReadInvoices()
    {
        var service = new StubInvoiceService();
        var controller = Controller(service, ApiKeyAuthenticationHandler.BuildPrincipal(
            new ApiKey { Id = 1, Name = "master", IsMaster = true }));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.GetStatus(default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.CreateInvoice(Request(), null, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.GetInvoice(Hash, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.ListInvoices(null, null, null, null, default)));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task IssuesForTheKeysMerchantAndPointsAtTheInvoice()
    {
        var service = new StubInvoiceService { Creation = new(InvoiceCreationOutcome.Created, Invoice()) };
        var controller = Controller(service);

        var result = await controller.CreateInvoice(Request(), "order-123-attempt-1", default);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(LightningInvoicesController.GetInvoice), created.ActionName);
        Assert.Equal(Hash, created.RouteValues!["paymentHash"]);
        var invoice = Assert.IsType<LightningInvoiceResponse>(created.Value);
        Assert.Equal(Hash, invoice.PaymentHash);
        Assert.Equal("lnbc-test", invoice.Bolt11);
        Assert.Equal(10_000, invoice.AmountSat);
        Assert.Equal(40m, invoice.Amount);
        Assert.Equal("BRL", invoice.Currency);
        Assert.Equal(Now.AddSeconds(600), invoice.ExpiresAt);
        Assert.Equal("order-123", invoice.ExternalId);

        var (issuer, _, idempotencyKey) = service.LastCreate!.Value;
        Assert.Equal(new InvoiceIssuer(MerchantId, KeyId), issuer);
        Assert.Equal("order-123-attempt-1", idempotencyKey);
        Assert.False(controller.Response.Headers.ContainsKey(LightningInvoicesController.IdempotentReplayedHeader));
    }

    [Fact]
    public async Task AReplayAnswersLikeTheOriginalAndSaysSo()
    {
        var controller = Controller(new StubInvoiceService { Creation = new(InvoiceCreationOutcome.Replayed, Invoice()) });

        var result = await controller.CreateInvoice(Request(), "order-123-attempt-1", default);

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        Assert.Equal("true", controller.Response.Headers[LightningInvoicesController.IdempotentReplayedHeader]);
    }

    [Theory]
    [InlineData(InvoiceCreationOutcome.Invalid, StatusCodes.Status400BadRequest)]
    [InlineData(InvoiceCreationOutcome.PriceUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(InvoiceCreationOutcome.BackendUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(InvoiceCreationOutcome.BackendFailure, StatusCodes.Status502BadGateway)]
    [InlineData(InvoiceCreationOutcome.IdempotencyMismatch, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(InvoiceCreationOutcome.IdempotencyInProgress, StatusCodes.Status409Conflict)]
    public async Task CreationOutcomesAreAnsweredWithTheirStatusCodes(InvoiceCreationOutcome outcome, int status)
    {
        var controller = Controller(new StubInvoiceService { Creation = new(outcome, Message: "why") });

        var result = await controller.CreateInvoice(Request(), "attempt-1", default);

        Assert.Equal(status, StatusOf(result));
        Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
    }

    [Theory]
    [InlineData(InvoiceCreationOutcome.BackendUnavailable)]
    [InlineData(InvoiceCreationOutcome.BackendFailure)]
    public async Task ABackendProblemTellsTheMerchantWhatWentWrong(InvoiceCreationOutcome outcome)
    {
        var controller = Controller(new StubInvoiceService
        {
            Creation = new(outcome, Message: "No Lightning backend is configured for this merchant.")
        });

        var result = await controller.CreateInvoice(Request(), "attempt-1", default);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal("No Lightning backend is configured for this merchant.", problem.Detail);
    }

    [Fact]
    public async Task AClientIsToldWhenToRetryARequestStillInProgress()
    {
        var controller = Controller(new StubInvoiceService { Creation = new(InvoiceCreationOutcome.IdempotencyInProgress) });

        await controller.CreateInvoice(Request(), "attempt-1", default);

        Assert.Equal("2", controller.Response.Headers.RetryAfter);
    }

    [Theory]
    [MemberData(nameof(MalformedIdempotencyKeys))]
    public async Task MalformedIdempotencyKeysAreRefusedBeforeAnythingHappens(string idempotencyKey)
    {
        var service = new StubInvoiceService();

        var result = await Controller(service).CreateInvoice(Request(), idempotencyKey, default);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [MemberData(nameof(WellFormedIdempotencyKeys))]
    public async Task OrdinaryIdempotencyKeysAreAccepted(string idempotencyKey)
    {
        var service = new StubInvoiceService { Creation = new(InvoiceCreationOutcome.Created, Invoice()) };

        var result = await Controller(service).CreateInvoice(Request(), idempotencyKey, default);

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        Assert.Equal(idempotencyKey, service.LastCreate!.Value.IdempotencyKey);
    }

    [Fact]
    public async Task ReadsAskForTheKeysMerchantsInvoice()
    {
        var paid = Invoice(LightningInvoiceStatus.Paid);
        paid.ReceivedSat = 10_000;
        paid.FeeSat = 21;
        paid.PaidAtUtc = Now.AddMinutes(1);
        var service = new StubInvoiceService { Read = new(InvoiceReadOutcome.Found, paid) };

        var result = await Controller(service).GetInvoice(Hash.ToUpperInvariant(), default);

        var status = Assert.IsType<LightningInvoiceStatusResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(LightningInvoiceStatusResponse.Paid, status.Status);
        Assert.True(status.IsPaid);
        Assert.Equal("order-123", status.ExternalId);
        Assert.Equal(40m, status.Amount);
        Assert.Equal("BRL", status.Currency);
        Assert.Equal(10_000, status.RequestedSat);
        Assert.Equal(10_000, status.ReceivedSat);
        Assert.Equal(21, status.FeeSat);
        Assert.Equal(Now.AddMinutes(1), status.CompletedAt);
        Assert.Equal((MerchantId, Hash.ToUpperInvariant()), service.LastRead);
    }

    [Theory]
    [InlineData(InvoiceReadOutcome.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(InvoiceReadOutcome.BackendUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(InvoiceReadOutcome.BackendFailure, StatusCodes.Status502BadGateway)]
    public async Task ReadOutcomesAreAnsweredWithTheirStatusCodes(InvoiceReadOutcome outcome, int status)
    {
        var controller = Controller(new StubInvoiceService { Read = new(outcome) });

        Assert.Equal(status, StatusOf(await controller.GetInvoice(Hash, default)));
    }

    [Fact]
    public async Task MalformedPaymentHashIsRefusedWithoutAskingTheService()
    {
        var service = new StubInvoiceService();

        var result = await Controller(service).GetInvoice("../getbalance", default);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task ListingPassesTheFiltersAndReturnsTheCursor()
    {
        var service = new StubInvoiceService
        {
            Page = ([Invoice(LightningInvoiceStatus.Paid, id: 9), Invoice(LightningInvoiceStatus.Paid, id: 8)], 8)
        };

        var result = await Controller(service).ListInvoices("PAID", "order-123", 2, 10, default);

        var page = Assert.IsType<LightningInvoicePage>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(LightningInvoiceStatusResponse.Paid, item.Status));
        Assert.Equal(8, page.NextCursor);
        Assert.Equal((MerchantId, new LightningInvoiceQuery(LightningInvoiceStatus.Paid, "order-123", 2, 10)), service.LastList);
    }

    [Theory]
    [InlineData("creating")]
    [InlineData("unpaid")]
    public async Task UnknownStatusFiltersAreRefused(string status)
    {
        var service = new StubInvoiceService();

        var result = await Controller(service).ListInvoices(status, null, null, null, default);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void ListingParametersAreBounded()
    {
        var parameters = typeof(LightningInvoicesController).GetMethod(nameof(LightningInvoicesController.ListInvoices))!
            .GetParameters().ToDictionary(p => p.Name!);

        var limit = parameters["limit"].GetCustomAttribute<RangeAttribute>()!;
        Assert.Equal(1, limit.Minimum);
        Assert.Equal(LightningInvoiceService.MaxPageSize, limit.Maximum);
        Assert.Equal(100, parameters["externalId"].GetCustomAttribute<StringLengthAttribute>()!.MaximumLength);
        Assert.NotNull(parameters["before"].GetCustomAttribute<RangeAttribute>());
    }

    [Fact]
    public async Task GatewayStatusIsTheKeysMerchants()
    {
        var service = new StubInvoiceService { Status = new LightningGatewayStatus { Enabled = true, Reachable = false } };

        var result = await Controller(service).GetStatus(default);

        var status = Assert.IsType<LightningGatewayStatus>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(status.Enabled);
        Assert.False(status.Reachable);
        Assert.Equal(MerchantId, service.LastStatus);
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

    private static LightningInvoicesController Controller(ILightningInvoiceService service, ClaimsPrincipal? user = null) =>
        new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user ?? ApiKeyAuthenticationHandler.BuildPrincipal(new ApiKey
                    {
                        Id = KeyId, Name = "Artcana checkout", MerchantId = MerchantId, Scopes = ApiKeyScopes.All
                    })
                }
            }
        };

    private static CreateLightningInvoiceRequest Request() =>
        new() { Amount = 40m, Currency = "BRL", ExternalId = "order-123" };

    private static LightningInvoice Invoice(LightningInvoiceStatus status = LightningInvoiceStatus.Pending, long id = 1) => new()
    {
        Id = id,
        MerchantId = MerchantId,
        ApiKeyId = KeyId,
        Status = status,
        PaymentHash = Hash,
        Bolt11 = "lnbc-test",
        ExternalId = "order-123",
        Description = "Payment of 40 BRL",
        Amount = 40m,
        Currency = "BRL",
        AmountSat = 10_000,
        BitcoinPrice = 400_000m,
        BitcoinPriceUsd = 80_000m,
        RateSources = 3,
        QuotedAtUtc = Now,
        ExpiresAtUtc = Now.AddSeconds(600),
        CreatedAtUtc = Now,
    };

    private static int? StatusOf(IActionResult result) => (result as IStatusCodeActionResult)?.StatusCode;

    private sealed class StubInvoiceService : ILightningInvoiceService
    {
        public LightningGatewayStatus Status { get; init; } = new() { Enabled = true, Reachable = true };
        public InvoiceCreationResult Creation { get; init; } = new(InvoiceCreationOutcome.BackendFailure);
        public InvoiceReadResult Read { get; init; } = new(InvoiceReadOutcome.NotFound);
        public (IReadOnlyList<LightningInvoice> Items, long? NextCursor) Page { get; init; } = ([], null);

        public int Calls { get; private set; }
        public int? LastStatus { get; private set; }

        public Task<LightningGatewayStatus> GetStatusAsync(int merchantId, CancellationToken cancellationToken)
        {
            Calls++;
            LastStatus = merchantId;
            return Task.FromResult(Status);
        }

        public (InvoiceIssuer Issuer, CreateLightningInvoiceRequest Request, string? IdempotencyKey)? LastCreate { get; private set; }
        public (int MerchantId, string PaymentHash)? LastRead { get; private set; }
        public (int MerchantId, LightningInvoiceQuery Query)? LastList { get; private set; }

        public Task<InvoiceCreationResult> CreateAsync(
            InvoiceIssuer issuer, CreateLightningInvoiceRequest request, string? idempotencyKey, CancellationToken cancellationToken)
        {
            Calls++;
            LastCreate = (issuer, request, idempotencyKey);
            return Task.FromResult(Creation);
        }

        public Task<InvoiceReadResult> GetAsync(int merchantId, string paymentHash, CancellationToken cancellationToken)
        {
            Calls++;
            LastRead = (merchantId, paymentHash);
            return Task.FromResult(Read);
        }

        public Task<(IReadOnlyList<LightningInvoice> Items, long? NextCursor)> ListAsync(
            int merchantId, LightningInvoiceQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            LastList = (merchantId, query);
            return Task.FromResult(Page);
        }
    }
}
