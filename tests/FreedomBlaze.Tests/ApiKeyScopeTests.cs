using FreedomBlaze.Authentication;
using FreedomBlaze.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FreedomBlaze.Tests;

/// <summary>
/// From an issued key to what it may call: the claims the handler builds, evaluated against the
/// policies the app registers.
/// </summary>
public sealed class ApiKeyScopeTests
{
    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddPublicApi(new ConfigurationBuilder().Build())
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    [Theory]
    [InlineData(ApiKeyScopes.Convert, ApiKeyPolicies.Convert, true)]
    [InlineData(ApiKeyScopes.Convert, ApiKeyPolicies.InvoicesWrite, false)]
    [InlineData(ApiKeyScopes.Convert, ApiKeyPolicies.InvoicesRead, false)]
    [InlineData(ApiKeyScopes.InvoicesRead, ApiKeyPolicies.InvoicesRead, true)]
    [InlineData(ApiKeyScopes.InvoicesRead, ApiKeyPolicies.InvoicesWrite, false)]
    [InlineData(ApiKeyScopes.InvoicesWrite | ApiKeyScopes.InvoicesRead, ApiKeyPolicies.Convert, false)]
    [InlineData(ApiKeyScopes.InvoicesWrite | ApiKeyScopes.InvoicesRead, ApiKeyPolicies.InvoicesWrite, true)]
    [InlineData(ApiKeyScopes.All, ApiKeyPolicies.Convert, true)]
    [InlineData(ApiKeyScopes.All, ApiKeyDefaults.MasterPolicy, false)]
    public async Task AKeyPassesExactlyThePoliciesOfItsScopes(ApiKeyScopes scopes, string policy, bool allowed)
    {
        var principal = ApiKeyAuthenticationHandler.BuildPrincipal(
            new ApiKey { Id = 7, Name = "Artcana checkout", MerchantId = 3, Scopes = scopes });

        Assert.Equal(allowed, (await Authorization.AuthorizeAsync(principal, policy)).Succeeded);
    }

    [Theory]
    [InlineData(ApiKeyPolicies.Convert)]
    [InlineData(ApiKeyPolicies.InvoicesWrite)]
    [InlineData(ApiKeyPolicies.InvoicesRead)]
    [InlineData(ApiKeyDefaults.MasterPolicy)]
    public async Task AMasterKeyPassesEveryPolicyWhateverItsStoredScopes(string policy)
    {
        var principal = ApiKeyAuthenticationHandler.BuildPrincipal(
            new ApiKey { Id = 1, Name = "master", IsMaster = true, Scopes = ApiKeyScopes.None });

        Assert.True((await Authorization.AuthorizeAsync(principal, policy)).Succeeded);
    }

    [Fact]
    public void ThePrincipalNamesTheKeyItsMerchantAndItsScopes()
    {
        var principal = ApiKeyAuthenticationHandler.BuildPrincipal(new ApiKey
        {
            Id = 7, Name = "Artcana checkout", MerchantId = 3, Scopes = ApiKeyScopes.InvoicesWrite | ApiKeyScopes.InvoicesRead
        });

        Assert.Equal(ApiKeyDefaults.Scheme, principal.Identity!.AuthenticationType);
        Assert.Equal("7", principal.FindFirst(ApiKeyDefaults.KeyIdClaim)!.Value);
        Assert.Equal("3", principal.FindFirst(ApiKeyDefaults.MerchantIdClaim)!.Value);
        Assert.Equal("false", principal.FindFirst(ApiKeyDefaults.MasterClaim)!.Value);
        Assert.Equal(new[] { "invoices:write", "invoices:read" },
            principal.FindAll(ApiKeyDefaults.ScopeClaim).Select(c => c.Value).ToArray());
    }

    [Fact]
    public void AKeyWithoutAMerchantCarriesNoMerchantClaim()
    {
        var principal = ApiKeyAuthenticationHandler.BuildPrincipal(
            new ApiKey { Id = 2, Name = "price widget", Scopes = ApiKeyScopes.Convert });

        Assert.Null(principal.FindFirst(ApiKeyDefaults.MerchantIdClaim));
        Assert.Equal(new[] { "convert" }, principal.FindAll(ApiKeyDefaults.ScopeClaim).Select(c => c.Value).ToArray());
    }

    [Fact]
    public void ScopeNamesParseLeniently()
    {
        Assert.True(ApiKeyScopeNames.TryParse(["Convert", " invoices:READ "], out var scopes, out var unknown));
        Assert.Equal(ApiKeyScopes.Convert | ApiKeyScopes.InvoicesRead, scopes);
        Assert.Null(unknown);

        Assert.False(ApiKeyScopeNames.TryParse(["convert", "payments:send"], out _, out unknown));
        Assert.Equal("payments:send", unknown);
    }
}
