using FreedomBlaze.Authentication;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Administrative endpoints for issuing, listing and revoking API keys. Authorized with the same
/// API-key structure as the public API: the caller must present a <b>master</b> key in the
/// <c>X-Api-Key</c> header (a master key authorizes key management; ordinary keys cannot reach these
/// endpoints). The first master key is seeded automatically on startup — see <c>Program.cs</c>.
/// </summary>
[ApiController]
[Route("api/admin/api-keys")]
[Tags("API Keys")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
[Produces("application/json")]
public sealed class ApiKeysController(IApiKeyService apiKeyService) : ControllerBase
{
    /// <summary>Issues a new API key. The plaintext secret is returned once and cannot be recovered.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateApiKeyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        var created = await apiKeyService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(List), new { }, created);
    }

    /// <summary>Returns all issued keys (non-secret view; plaintext secrets are never stored).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ApiKeyInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await apiKeyService.ListAsync(cancellationToken));

    /// <summary>Revokes (soft-deletes) an API key by its database ID.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(int id, CancellationToken cancellationToken)
    {
        var revoked = await apiKeyService.RevokeAsync(id, cancellationToken);
        return revoked ? NoContent() : NotFound();
    }
}
