using System.Security.Cryptography;
using System.Text;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Administrative endpoints for issuing, listing and revoking API keys. Guarded by a separate admin
/// token (<c>ApiKeys:AdminToken</c>) sent in the <c>X-Admin-Token</c> header — issuing keys is a
/// privileged operation, kept distinct from the issued keys themselves. When no admin token is
/// configured the whole controller is disabled (returns 404) so keys cannot be minted over HTTP.
/// </summary>
[ApiController]
[Route("api/admin/api-keys")]
[Produces("application/json")]
public sealed class ApiKeysController(IApiKeyService apiKeyService, IOptions<ApiKeyOptions> options) : ControllerBase
{
    private const string AdminTokenHeader = "X-Admin-Token";
    private readonly ApiKeyOptions _options = options.Value;

    [HttpPost]
    [ProducesResponseType(typeof(CreateApiKeyResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
            return NotFound();

        var created = await apiKeyService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(List), new { }, created);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ApiKeyInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
            return NotFound();

        return Ok(await apiKeyService.ListAsync(cancellationToken));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(int id, CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
            return NotFound();

        var revoked = await apiKeyService.RevokeAsync(id, cancellationToken);
        return revoked ? NoContent() : NotFound();
    }

    /// <summary>
    /// True only when an admin token is configured and the request presents a matching one. The
    /// comparison is constant-time to avoid leaking the token through timing.
    /// </summary>
    private bool IsAuthorized()
    {
        if (string.IsNullOrWhiteSpace(_options.AdminToken))
            return false;

        if (!Request.Headers.TryGetValue(AdminTokenHeader, out var presented))
            return false;

        var a = Encoding.UTF8.GetBytes(presented.ToString());
        var b = Encoding.UTF8.GetBytes(_options.AdminToken);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
