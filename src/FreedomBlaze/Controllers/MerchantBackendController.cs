using FreedomBlaze.Authentication;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

/// <summary>
/// The Lightning node a merchant's invoices are issued on. Master key only. A merchant registered after
/// backends existed has none until one is configured here, and cannot issue invoices until then.
/// </summary>
[ApiController]
[Route("api/admin/merchants/{merchantId:int}/backend")]
[Tags("Merchants")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
[Produces("application/json")]
public sealed class MerchantBackendController(IMerchantService merchants, ILightningBackendService backends) : ControllerBase
{
    /// <summary>The merchant's current backend. The password is never returned.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(LightningBackendInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int merchantId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        return await backends.GetAsync(merchantId, cancellationToken) is { } backend ? Ok(backend) : NoBackend(merchantId);
    }

    /// <summary>
    /// Sets the merchant's node, retiring the previous one (its invoices stay readable). The node is checked
    /// first and its public key pinned. For <c>phoenixd</c>, give the URL and the node's
    /// <c>http-password-limited-access</c>; the full-access password is refused.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(LightningBackendInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Configure(int merchantId, ConfigureLightningBackendRequest request, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        try
        {
            return Ok(await backends.ConfigureAsync(merchantId, request, cancellationToken));
        }
        catch (RequestValidationException ex)
        {
            return Problem(title: "Invalid backend request", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (LightningBackendCheckException ex)
        {
            return Problem(title: "Backend check failed", detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>Checks the merchant's node now and records the result, including whether it is still the pinned node.</summary>
    [HttpPost("check")]
    [ProducesResponseType(typeof(LightningBackendInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Check(int merchantId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        return await backends.CheckAsync(merchantId, cancellationToken) is { } backend ? Ok(backend) : NoBackend(merchantId);
    }

    /// <summary>Retires the merchant's backend: no new invoices until another is configured.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(int merchantId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        return await backends.RemoveAsync(merchantId, cancellationToken) ? NoContent() : NoBackend(merchantId);
    }

    private ObjectResult MerchantNotFound(int merchantId) =>
        Problem(title: "Merchant not found", detail: $"Merchant {merchantId} does not exist.", statusCode: StatusCodes.Status404NotFound);

    private ObjectResult NoBackend(int merchantId) =>
        Problem(title: "No backend", detail: $"Merchant {merchantId} has no Lightning backend.", statusCode: StatusCodes.Status404NotFound);
}
