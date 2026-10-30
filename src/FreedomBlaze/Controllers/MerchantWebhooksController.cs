using System.ComponentModel.DataAnnotations;
using FreedomBlaze.Authentication;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using FreedomBlaze.Services.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

/// <summary>
/// A merchant's webhook endpoints and their delivery log. Master key only. The gateway posts
/// <c>invoice.paid</c> and <c>invoice.expired</c> to every active endpoint, signed per Standard Webhooks with
/// the endpoint's own secret, retrying for about a day.
/// </summary>
[ApiController]
[Route("api/admin/merchants/{merchantId:int}")]
[Tags("Merchants")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
[Produces("application/json")]
public sealed class MerchantWebhooksController(IMerchantService merchants, IWebhookEndpointService webhooks) : ControllerBase
{
    /// <summary>Registers an endpoint. Its signing secret is in this response only.</summary>
    [HttpPost("webhooks")]
    [ProducesResponseType(typeof(WebhookEndpointWithSecret), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int merchantId, CreateWebhookEndpointRequest request, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        try
        {
            var created = await webhooks.CreateAsync(merchantId, request, cancellationToken);
            return CreatedAtAction(nameof(List), new { merchantId }, created);
        }
        catch (RequestValidationException ex)
        {
            return Invalid(ex.Message);
        }
    }

    /// <summary>The merchant's endpoints, active or not. Secrets are never listed.</summary>
    [HttpGet("webhooks")]
    [ProducesResponseType(typeof(IReadOnlyList<WebhookEndpointInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(int merchantId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        return Ok(await webhooks.ListAsync(merchantId, cancellationToken));
    }

    /// <summary>Changes an endpoint's URL or note, or (de)activates it. Disabling fails its pending deliveries.</summary>
    [HttpPatch("webhooks/{endpointId:int}")]
    [ProducesResponseType(typeof(WebhookEndpointInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int merchantId, int endpointId, UpdateWebhookEndpointRequest request, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        try
        {
            return await webhooks.UpdateAsync(merchantId, endpointId, request, cancellationToken) is { } endpoint
                ? Ok(endpoint)
                : EndpointNotFound(endpointId);
        }
        catch (RequestValidationException ex)
        {
            return Invalid(ex.Message);
        }
    }

    /// <summary>Disables an endpoint. It is kept, with its delivery log, and can be enabled again.</summary>
    [HttpDelete("webhooks/{endpointId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(int merchantId, int endpointId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        return await webhooks.UpdateAsync(merchantId, endpointId, new UpdateWebhookEndpointRequest { IsActive = false }, cancellationToken) is null
            ? EndpointNotFound(endpointId)
            : NoContent();
    }

    /// <summary>Replaces the endpoint's signing secret, effective from the next attempt. The new secret is in this response only.</summary>
    [HttpPost("webhooks/{endpointId:int}/rotate-secret")]
    [ProducesResponseType(typeof(WebhookEndpointWithSecret), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateSecret(int merchantId, int endpointId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        try
        {
            return await webhooks.RotateSecretAsync(merchantId, endpointId, cancellationToken) is { } endpoint
                ? Ok(endpoint)
                : EndpointNotFound(endpointId);
        }
        catch (RequestValidationException ex)
        {
            return Invalid(ex.Message);
        }
    }

    /// <summary>Posts a signed <c>webhook.test</c> event now and reports the endpoint's answer. Nothing is retried.</summary>
    [HttpPost("webhooks/{endpointId:int}/test")]
    [ProducesResponseType(typeof(WebhookTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Test(int merchantId, int endpointId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        return await webhooks.SendTestAsync(merchantId, endpointId, cancellationToken) is { } result
            ? Ok(result)
            : EndpointNotFound(endpointId);
    }

    /// <summary>The merchant's deliveries, newest first. Pass a page's <c>nextCursor</c> as <c>before</c> for the next page.</summary>
    /// <param name="merchantId">The merchant.</param>
    /// <param name="state"><c>pending</c>, <c>delivered</c> or <c>failed</c>.</param>
    /// <param name="endpointId">Only deliveries to this endpoint.</param>
    /// <param name="limit">Page size, 1 to 100 (default 50).</param>
    /// <param name="before">The previous page's <c>nextCursor</c>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    [HttpGet("webhook-deliveries")]
    [ProducesResponseType(typeof(WebhookDeliveryPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListDeliveries(
        int merchantId,
        [FromQuery] string? state,
        [FromQuery] int? endpointId,
        [FromQuery, Range(1, WebhookEndpointService.MaxPageSize)] int? limit,
        [FromQuery, Range(1, long.MaxValue)] long? before,
        CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        WebhookDeliveryState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!WebhookDeliveryInfo.TryParseState(state, out var parsed))
                return Invalid("state must be pending, delivered or failed.");
            stateFilter = parsed;
        }

        return Ok(await webhooks.ListDeliveriesAsync(merchantId,
            new WebhookDeliveryQuery(stateFilter, endpointId, limit, before), cancellationToken));
    }

    /// <summary>Queues a delivery's event again, with the same <c>webhook-id</c>, as a new delivery.</summary>
    [HttpPost("webhook-deliveries/{deliveryId:long}/redeliver")]
    [ProducesResponseType(typeof(WebhookDeliveryInfo), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Redeliver(int merchantId, long deliveryId, CancellationToken cancellationToken)
    {
        if (await merchants.GetAsync(merchantId, cancellationToken) is null)
            return MerchantNotFound(merchantId);

        try
        {
            return await webhooks.RedeliverAsync(merchantId, deliveryId, cancellationToken) is { } delivery
                ? Accepted(delivery)
                : Problem(title: "Delivery not found", detail: $"Merchant {merchantId} has no delivery {deliveryId}.",
                    statusCode: StatusCodes.Status404NotFound);
        }
        catch (RequestValidationException ex)
        {
            return Invalid(ex.Message);
        }
    }

    private ObjectResult MerchantNotFound(int merchantId) =>
        Problem(title: "Merchant not found", detail: $"Merchant {merchantId} does not exist.", statusCode: StatusCodes.Status404NotFound);

    private ObjectResult EndpointNotFound(int endpointId) =>
        Problem(title: "Endpoint not found", detail: $"The merchant has no webhook endpoint {endpointId}.", statusCode: StatusCodes.Status404NotFound);

    private ObjectResult Invalid(string detail) =>
        Problem(title: "Invalid webhook request", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
