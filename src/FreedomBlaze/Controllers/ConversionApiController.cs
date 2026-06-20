using FreedomBlaze.Authentication;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Public, key-authenticated conversion API. External clients send their <c>X-Api-Key</c> and a
/// conversion request; responses are derived from the same aggregated rates that power the on-site
/// Sats Converter. Throttled per key via the <c>ApiKeyPolicy</c> rate-limiter.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]
[EnableRateLimiting(RateLimitPolicies.PerApiKey)]
[Produces("application/json")]
public sealed class ConversionApiController(ICurrencyConversionService conversionService) : ControllerBase
{
    /// <summary>Converts an amount between fiat currencies and Bitcoin units (BTC/SATS).</summary>
    [HttpPost("convert")]
    [ProducesResponseType(typeof(ConversionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ConversionResponse>> Convert(ConversionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await conversionService.ConvertAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ConversionException ex) when (ex.IsClientError)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ConversionException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Lists the currency/unit codes accepted by <see cref="Convert"/>.</summary>
    [HttpGet("currencies")]
    [ProducesResponseType(typeof(IReadOnlyCollection<string>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyCollection<string>> Currencies() => Ok(conversionService.SupportedCurrencies);
}
