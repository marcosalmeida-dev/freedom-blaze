using System.Reflection;
using FreedomBlaze.Authentication;
using FreedomBlaze.Controllers;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreedomBlaze.Tests;

public sealed class MerchantWebhooksControllerTests
{
    [Fact]
    public void OnlyAMasterKeyManagesWebhooks()
    {
        var authorization = Assert.Single(typeof(MerchantWebhooksController).GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        Assert.Equal(ApiKeyDefaults.MasterPolicy, authorization.Policy);
    }

    [Fact]
    public async Task AnUnknownMerchantIsNotFoundWhateverTheCall()
    {
        using var fixture = new GatewayFixture();
        var controller = Controller(fixture);

        Assert.Equal(404, StatusOf(await controller.Create(99, new CreateWebhookEndpointRequest { Url = GatewayFixture.EndpointUrl }, default)));
        Assert.Equal(404, StatusOf(await controller.List(99, default)));
        Assert.Equal(404, StatusOf(await controller.ListDeliveries(99, null, null, null, null, default)));
        Assert.Equal(404, StatusOf(await controller.Redeliver(99, 1, default)));
    }

    [Fact]
    public async Task AnEndpointIsCreatedWithItsSecretAndManagedById()
    {
        using var fixture = new GatewayFixture();
        var controller = Controller(fixture);

        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.Create(fixture.MerchantId, new CreateWebhookEndpointRequest { Url = GatewayFixture.EndpointUrl }, default));
        var endpoint = Assert.IsType<WebhookEndpointWithSecret>(created.Value);
        var tested = Assert.IsType<OkObjectResult>(await controller.Test(fixture.MerchantId, endpoint.Id, default));
        var rotated = Assert.IsType<OkObjectResult>(await controller.RotateSecret(fixture.MerchantId, endpoint.Id, default));

        Assert.StartsWith("whsec_", endpoint.Secret);
        Assert.True(Assert.IsType<WebhookTestResult>(tested.Value).Delivered);
        Assert.NotEqual(endpoint.Secret, Assert.IsType<WebhookEndpointWithSecret>(rotated.Value).Secret);
        Assert.Equal(204, StatusOf(await controller.Disable(fixture.MerchantId, endpoint.Id, default)));
        Assert.Equal(404, StatusOf(await controller.Disable(fixture.MerchantId, endpoint.Id + 1, default)));
        Assert.Equal(404, StatusOf(await controller.Test(fixture.MerchantId, endpoint.Id + 1, default)));
    }

    [Fact]
    public async Task RefusalsKeepTheirReason()
    {
        using var fixture = new GatewayFixture();
        var controller = Controller(fixture);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(
            await controller.Create(fixture.MerchantId, new CreateWebhookEndpointRequest { Url = "http://shop.example/hooks" }, default)).Value);
        var badState = await controller.ListDeliveries(fixture.MerchantId, "lost", null, null, null, default);

        Assert.Equal(400, problem.Status);
        Assert.Equal("url must use HTTPS.", problem.Detail);
        Assert.Equal(400, StatusOf(badState));
    }

    [Fact]
    public async Task ADeliveryIsListedAndRedelivered()
    {
        using var fixture = new GatewayFixture();
        await fixture.AddEndpointAsync();
        var invoice = await fixture.IssueAsync();
        fixture.Node.Pay(invoice.PaymentHash!);
        fixture.Clock.Now = GatewayFixture.Start.AddSeconds(5);
        await fixture.Sweeper.SweepAsync(default);
        var controller = Controller(fixture);

        var page = Assert.IsType<WebhookDeliveryPage>(Assert.IsType<OkObjectResult>(
            await controller.ListDeliveries(fixture.MerchantId, "pending", null, null, null, default)).Value);
        var redelivered = await controller.Redeliver(fixture.MerchantId, page.Items[0].Id, default);

        Assert.Equal("invoice.paid", Assert.Single(page.Items).EventType);
        Assert.Equal(StatusCodes.Status202Accepted, StatusOf(redelivered));
        Assert.Equal(404, StatusOf(await controller.Redeliver(fixture.MerchantId, 9_999, default)));
    }

    private static MerchantWebhooksController Controller(GatewayFixture fixture) =>
        new(new MerchantService(fixture.Db.Repository<Merchant>(), fixture.Clock, NullLogger<MerchantService>.Instance), fixture.Endpoints)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static int? StatusOf(IActionResult result) => (result as IStatusCodeActionResult)?.StatusCode;
}
