using System.Net;
using FreedomBlaze.Authentication;
using FreedomBlaze.Services.Lightning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Phoenixd.NET.Interfaces;

namespace FreedomBlaze.Tests;

/// <summary>
/// The service defaults give every HTTP client a retrying resilience handler. Calls that issue invoices or
/// move money must opt out: a retried request whose first attempt went through does it twice.
/// </summary>
public sealed class HttpClientRetryTests
{
    [Fact]
    public async Task TheDefaultsRetryAFailedPostSoTheOptOutsBelowMatter()
    {
        var node = new CountingHandler();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging();
        services.AddHttpClient("plain").ConfigurePrimaryHttpMessageHandler(() => node);
        AddServiceDefaultsResilience(services);
        await using var provider = services.BuildServiceProvider();

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("plain")
            .PostAsync("https://node.example/createinvoice", new StringContent(string.Empty));

        Assert.True(node.Calls > 1);
    }

    [Fact]
    public async Task InvoicesOnAMerchantsNodeAreNeverRetried()
    {
        var node = new CountingHandler();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging();
        services.AddPublicApi(new ConfigurationBuilder().Build());
        services.AddHttpClient(LightningBackendFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => node);
        AddServiceDefaultsResilience(services);
        await using var provider = services.BuildServiceProvider();

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient(LightningBackendFactory.HttpClientName)
            .PostAsync("https://node.example/createinvoice", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, node.Calls);
    }

    [Fact]
    public async Task PaymentsFromTheOperatorsNodeAreNeverRetried()
    {
        var node = new CountingHandler();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging();
        services.AddOperatorNode(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PhoenixConfig:Host"] = "https://phoenixd.example",
            ["PhoenixConfig:Token"] = "test-password",
        }).Build());
        services.AddHttpClient(nameof(IPaymentService)).ConfigurePrimaryHttpMessageHandler(() => node);
        AddServiceDefaultsResilience(services);
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.GetRequiredService<IPaymentService>().SendLightningInvoice(1_000, "lnbc10u1-test"));

        Assert.Equal(1, node.Calls);
    }

    /// <summary>What <c>AddServiceDefaults</c> does to every client, with the retry delay removed to keep the test fast.</summary>
    private static void AddServiceDefaultsResilience(IServiceCollection services) =>
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
