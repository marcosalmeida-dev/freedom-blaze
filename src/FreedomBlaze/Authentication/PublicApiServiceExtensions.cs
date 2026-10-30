using System.Threading.RateLimiting;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using FreedomBlaze.Services.Lightning;
using FreedomBlaze.Services.Webhooks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Authentication;

/// <summary>Named rate-limiting policies.</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-API-key fixed-window throttle applied to the public conversion API.</summary>
    public const string PerApiKey = "ApiKeyPolicy";

    /// <summary>
    /// Per-client-IP fixed-window throttle for anonymous public endpoints (e.g. Bitcoin news reads),
    /// which can trigger expensive paid work and so must be bounded for unauthenticated callers.
    /// </summary>
    public const string PerIp = "PerIpPolicy";

    /// <summary>
    /// Stricter per-client-IP throttle for the anonymous contact endpoint, which fans out to an
    /// external Telegram channel and so is a spam/abuse target.
    /// </summary>
    public const string ContactSubmit = "ContactSubmitPolicy";
}

/// <summary>
/// Wires up everything the public, key-authenticated API needs: the API-key services, the
/// authentication scheme, an authorization policy, and a per-key rate limiter. Keeps the
/// registration in one place so <c>Program.cs</c> stays readable.
/// </summary>
public static class PublicApiServiceExtensions
{
    public static IServiceCollection AddPublicApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApiKeyOptions>(configuration.GetSection(ApiKeyOptions.Section));

        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<IMerchantService, MerchantService>();
        services.AddSingleton<ICurrencyConversionService, CurrencyConversionService>();

        // Merchants' Lightning nodes. A malformed credential key stops startup instead of a later request.
        services.AddOptions<LightningBackendOptions>()
            .Bind(configuration.GetSection(LightningBackendOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LightningBackendOptions>, LightningBackendOptionsValidator>();
        services.AddSingleton<ILightningCredentialProtector, AesGcmCredentialProtector>();
        services.AddSingleton<ILightningBackendFactory, LightningBackendFactory>();
        services.AddSingleton<ILightningBackendService, LightningBackendService>();
        services.AddSingleton<ILightningInvoiceService, LightningInvoiceService>();

        // Background confirmation of pending invoices and the merchants' webhooks.
        services.Configure<LightningWebhookOptions>(configuration.GetSection(LightningWebhookOptions.Section));
        services.Configure<LightningWorkerOptions>(configuration.GetSection(LightningWorkerOptions.Section));
        services.AddSingleton<IWebhookOutbox, WebhookOutbox>();
        services.AddSingleton<LightningInvoiceRefresher>();
        services.AddSingleton<LightningInvoiceSweeper>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddSingleton<IWebhookEndpointService, WebhookEndpointService>();
        services.AddHostedService<LightningGatewayWorker>();

        // No redirects: a node's password is only ever sent to the address it was configured for. No retries
        // either, including the ones the service defaults give every client: a retried createinvoice issues a
        // second invoice behind the idempotency reservation's back.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it is the only way to opt out of the defaults.
        services.AddHttpClient(LightningBackendFactory.HttpClientName)
            .ConfigureHttpClient((provider, client) => client.Timeout =
                TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<LightningBackendOptions>>().Value.RequestTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();

        // Webhooks keep their own retry schedule and delivery log, so the client must not retry on its own, and a
        // redirect would send a signed event somewhere nobody configured.
        services.AddHttpClient(WebhookDispatcher.HttpClientName)
            .ConfigureHttpClient((provider, client) => client.Timeout =
                TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<LightningWebhookOptions>>().Value.RequestTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });

        services.AddAuthorization(options =>
        {
            // Key-management endpoints require a master key (same auth scheme, master claim set).
            options.AddPolicy(ApiKeyDefaults.MasterPolicy, policy => policy
                .AddAuthenticationSchemes(ApiKeyDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(ApiKeyDefaults.MasterClaim, "true"));

            // One policy per scope; master keys carry every scope claim, so they pass them all.
            foreach (var (name, scope) in ApiKeyPolicies.All)
            {
                options.AddPolicy(name, policy => policy
                    .AddAuthenticationSchemes(ApiKeyDefaults.Scheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim(ApiKeyDefaults.ScopeClaim, scope));
            }
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicies.PerApiKey, httpContext =>
            {
                // Authentication has already run (the endpoint requires it), so the key id and its
                // budget are available; fall back defensively if not.
                var partition = httpContext.Items[ApiKeyDefaults.PartitionItemKey] as string ?? "unauthenticated";
                var permitLimit = httpContext.Items[ApiKeyDefaults.RateLimitItemKey] as int? ?? 60;

                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });

            // Anonymous public reads: throttle per client IP so unauthenticated callers cannot drive
            // unbounded (and, for news generation, paid) work by enumerating inputs.
            options.AddPolicy(RateLimitPolicies.PerIp, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            // Contact form: stricter per-IP budget — it relays to an external Telegram channel.
            options.AddPolicy(RateLimitPolicies.ContactSubmit, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.OnRejected = (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }
}
