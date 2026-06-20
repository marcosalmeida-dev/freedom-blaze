using System.Threading.RateLimiting;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authentication;

namespace FreedomBlaze.Authentication;

/// <summary>Named rate-limiting policies.</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-API-key fixed-window throttle applied to the public conversion API.</summary>
    public const string PerApiKey = "ApiKeyPolicy";
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
        services.AddSingleton<ICurrencyConversionService, CurrencyConversionService>();

        services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });

        services.AddAuthorization();

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
