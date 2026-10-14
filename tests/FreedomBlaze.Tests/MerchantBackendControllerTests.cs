using System.Reflection;
using FreedomBlaze.Authentication;
using FreedomBlaze.Controllers;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace FreedomBlaze.Tests;

public sealed class MerchantBackendControllerTests
{
    [Fact]
    public void OnlyAMasterKeyManagesBackends()
    {
        var authorization = Assert.Single(typeof(MerchantBackendController).GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        Assert.Equal(ApiKeyDefaults.MasterPolicy, authorization.Policy);
        Assert.Equal(ApiKeyDefaults.Scheme, authorization.AuthenticationSchemes);
    }

    [Fact]
    public async Task AnUnknownMerchantIsNotFoundWhateverTheCall()
    {
        var backends = new StubBackends();
        var controller = Controller(backends, merchantExists: false);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.Get(9, default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.Configure(9, new() { Kind = "operator" }, default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.Check(9, default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.Remove(9, default)));
        Assert.Equal(0, backends.Calls);
    }

    [Fact]
    public async Task AMerchantWithoutABackendSaysSo()
    {
        var controller = Controller(new StubBackends());

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(await controller.Get(3, default)).Value);

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal("Merchant 3 has no Lightning backend.", problem.Detail);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.Remove(3, default)));
    }

    [Fact]
    public async Task ConfiguringAnswersWithTheStoredBackend()
    {
        var info = new LightningBackendInfo { Id = 5, Kind = LightningBackendInfo.PhoenixdKind, IsActive = true };
        var controller = Controller(new StubBackends { Configured = info });

        var result = await controller.Configure(3, new() { Kind = "phoenixd", Url = "https://node.example", Password = "limited" }, default);

        Assert.Same(info, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Theory]
    [InlineData(false, StatusCodes.Status400BadRequest, "Invalid backend request")]
    [InlineData(true, StatusCodes.Status422UnprocessableEntity, "Backend check failed")]
    public async Task RefusalsKeepTheirReason(bool checkFailed, int status, string title)
    {
        Exception refusal = checkFailed
            ? new LightningBackendCheckException("The node refused the password.")
            : new RequestValidationException("url must use HTTPS.");
        var controller = Controller(new StubBackends { Refusal = refusal });

        var result = await controller.Configure(3, new() { Kind = "phoenixd" }, default);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal(refusal.Message, problem.Detail);
    }

    private static MerchantBackendController Controller(StubBackends backends, bool merchantExists = true) =>
        new(new StubMerchants(merchantExists), backends)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static int? StatusOf(IActionResult result) => (result as IStatusCodeActionResult)?.StatusCode;

    private sealed class StubMerchants(bool exists) : IMerchantService
    {
        public Task<MerchantInfo> CreateAsync(CreateMerchantRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MerchantInfo>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MerchantInfo?> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(exists ? new MerchantInfo { Id = id, Name = "Artcana", IsActive = true } : null);

        public Task<MerchantInfo?> UpdateAsync(int id, UpdateMerchantRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubBackends : ILightningBackendService
    {
        public LightningBackendInfo? Current { get; init; }
        public LightningBackendInfo? Configured { get; init; }
        public Exception? Refusal { get; init; }
        public int Calls { get; private set; }

        public Task<LightningBackendInfo?> GetAsync(int merchantId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Current);
        }

        public Task<LightningBackendInfo> ConfigureAsync(int merchantId, ConfigureLightningBackendRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Refusal is null ? Task.FromResult(Configured!) : Task.FromException<LightningBackendInfo>(Refusal);
        }

        public Task<LightningBackendInfo?> CheckAsync(int merchantId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Current);
        }

        public Task<bool> RemoveAsync(int merchantId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Current is not null);
        }
    }
}
