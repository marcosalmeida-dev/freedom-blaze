using FreedomBlaze.Authentication;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Administrative endpoints for the merchants the gateway takes payments for. Master key only, like key
/// management. Issue a merchant's integration a key with <c>POST /api/admin/api-keys</c> and its
/// <c>merchantId</c>; deactivating the merchant here stops all of its keys at once.
/// </summary>
[ApiController]
[Route("api/admin/merchants")]
[Tags("Merchants")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
[Produces("application/json")]
public sealed class MerchantsController(IMerchantService merchantService) : ControllerBase
{
    /// <summary>Registers a merchant.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(MerchantInfo), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(CreateMerchantRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var merchant = await merchantService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = merchant.Id }, merchant);
        }
        catch (RequestValidationException ex)
        {
            return Invalid(ex.Message);
        }
    }

    /// <summary>Lists every merchant, active or not.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MerchantInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await merchantService.ListAsync(cancellationToken));

    /// <summary>Returns one merchant.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(MerchantInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        await merchantService.GetAsync(id, cancellationToken) is { } merchant ? Ok(merchant) : NotFound();

    /// <summary>Renames a merchant or (de)activates it. Deactivation stops every key bound to it.</summary>
    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(MerchantInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, UpdateMerchantRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await merchantService.UpdateAsync(id, request, cancellationToken) is { } merchant ? Ok(merchant) : NotFound();
        }
        catch (RequestValidationException ex)
        {
            return Invalid(ex.Message);
        }
    }

    private ObjectResult Invalid(string detail) =>
        Problem(title: "Invalid merchant request", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
