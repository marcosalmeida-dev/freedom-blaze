using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Authentication;

/// <summary>
/// Authenticates requests by validating the secret in the <c>X-Api-Key</c> header (or
/// <c>Authorization: Bearer &lt;key&gt;</c>) against the issued keys in the database. On success it
/// builds a principal carrying the key's id/name and stashes the per-key rate limit and partition id
/// in <see cref="HttpContext.Items"/> for the rate limiter. Failures return a clean 401 without
/// leaking why the key was rejected.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IApiKeyService apiKeyService,
    IOptions<ApiKeyOptions> keyOptions)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";
    private readonly ApiKeyOptions _keyOptions = keyOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ExtractKey();
        if (string.IsNullOrEmpty(presented))
            return AuthenticateResult.NoResult();

        var key = await apiKeyService.ValidateAsync(presented, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Invalid or expired API key.");

        var principal = BuildPrincipal(key);

        // Hand the rate limiter a stable partition (the key id) and this key's budget.
        Context.Items[ApiKeyDefaults.PartitionItemKey] = key.Id.ToString();
        Context.Items[ApiKeyDefaults.RateLimitItemKey] =
            key.RateLimitPerMinute ?? _keyOptions.DefaultRateLimitPerMinute;

        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyDefaults.Scheme));
    }

    /// <summary>
    /// The principal for an authenticated key: its id, name and master flag, the merchant it acts for,
    /// and one claim per scope. Master keys carry every scope.
    /// </summary>
    internal static ClaimsPrincipal BuildPrincipal(ApiKey key)
    {
        var claims = new List<Claim>
        {
            new(ApiKeyDefaults.KeyIdClaim, key.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, key.Name),
            new(ApiKeyDefaults.MasterClaim, key.IsMaster ? "true" : "false"),
        };

        if (key.MerchantId is { } merchantId)
            claims.Add(new Claim(ApiKeyDefaults.MerchantIdClaim, merchantId.ToString(CultureInfo.InvariantCulture)));

        foreach (var scope in ApiKeyScopeNames.ToNames(key.IsMaster ? ApiKeyScopes.All : key.Scopes))
            claims.Add(new Claim(ApiKeyDefaults.ScopeClaim, scope));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyDefaults.Scheme));
    }

    /// <summary>Adds a WWW-Authenticate hint so clients know which header to send.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.HeaderName}\"";
        return base.HandleChallengeAsync(properties);
    }

    private string? ExtractKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var header))
        {
            var value = header.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        // Fallback: Authorization: Bearer <key>.
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return authorization[BearerPrefix.Length..].Trim();

        return null;
    }
}
