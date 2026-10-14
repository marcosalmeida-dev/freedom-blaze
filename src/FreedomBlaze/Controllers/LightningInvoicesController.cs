using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using FreedomBlaze.Authentication;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Fiat-priced Lightning invoices for merchants' integrations (e.g. a store checkout). Unlike the
/// operator API at <c>/api/payment-manager</c>, which needs a master key because it can move funds,
/// these endpoints can only <b>receive</b>: they quote an amount in satoshis, create the invoice on the
/// merchant's own Lightning backend in the same request, and report its status.
/// <para>
/// Every invoice belongs to the merchant of the key that issued it, and is recorded by the gateway.
/// Keys need the <c>invoices:write</c> scope to issue and <c>invoices:read</c> to read; any key of the
/// merchant can read the merchant's invoices, and another merchant's invoice answers exactly like an
/// unknown one.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/lightning")]
[Tags("Lightning Invoices")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]
[EnableRateLimiting(RateLimitPolicies.PerApiKey)]
[Produces("application/json")]
public sealed partial class LightningInvoicesController(ILightningInvoiceService invoiceService) : ControllerBase
{
    /// <summary>Request header that makes invoice creation safe to retry.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Response header marking a replayed creation.</summary>
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    /// <summary>
    /// Reports whether the key's merchant can take Lightning payments right now (a backend is configured
    /// and its pinned node answers), so a checkout can hide the option when not.
    /// </summary>
    [HttpGet("status")]
    [Authorize(Policy = ApiKeyPolicies.InvoicesWrite)]
    [ProducesResponseType(typeof(LightningGatewayStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        if (MerchantId() is not { } merchantId)
            return NoMerchant();

        return Ok(await invoiceService.GetStatusAsync(merchantId, cancellationToken));
    }

    /// <summary>
    /// Quotes the amount in satoshis (rounded up) and creates a BOLT11 invoice for it. The quote
    /// holds for the invoice's lifetime.
    /// <para>
    /// Send an <c>Idempotency-Key</c> header (up to 100 visible ASCII characters, unique per
    /// payment attempt) to make the call safe to retry: a repeat of the same request returns the
    /// original invoice with <c>Idempotent-Replayed: true</c> instead of issuing a second one.
    /// </para>
    /// </summary>
    [HttpPost("invoices")]
    [Authorize(Policy = ApiKeyPolicies.InvoicesWrite)]
    [ProducesResponseType(typeof(LightningInvoiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateInvoice(
        CreateLightningInvoiceRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (MerchantId() is not { } merchantId)
            return NoMerchant();

        if (idempotencyKey is not null && !IdempotencyKeyPattern().IsMatch(idempotencyKey))
            return Invalid($"{IdempotencyKeyHeader} must be 1 to 100 visible ASCII characters.");

        var result = await invoiceService.CreateAsync(
            new InvoiceIssuer(merchantId, KeyId()), request, idempotencyKey, cancellationToken);

        switch (result.Outcome)
        {
            case InvoiceCreationOutcome.Created or InvoiceCreationOutcome.Replayed when result.Invoice is { } invoice:
                if (result.Outcome == InvoiceCreationOutcome.Replayed)
                    Response.Headers[IdempotentReplayedHeader] = "true";
                return CreatedAtAction(nameof(GetInvoice), new { paymentHash = invoice.PaymentHash },
                    LightningInvoiceResponse.From(invoice));

            case InvoiceCreationOutcome.Invalid:
                return Invalid(result.Message ?? "The invoice request is invalid.");

            case InvoiceCreationOutcome.PriceUnavailable:
                return Problem(title: "Price unavailable", detail: result.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            case InvoiceCreationOutcome.BackendUnavailable:
                return Unavailable(result.Message);

            case InvoiceCreationOutcome.IdempotencyMismatch:
                return Problem(title: "Idempotency-Key reused", detail: result.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            case InvoiceCreationOutcome.IdempotencyInProgress:
                Response.Headers.RetryAfter = "2";
                return Problem(title: "Request in progress",
                    detail: "A request with this Idempotency-Key is still being processed. Retry shortly.",
                    statusCode: StatusCodes.Status409Conflict);

            default:
                return NodeFailure(result.Message);
        }
    }

    /// <summary>
    /// Reads one of the merchant's invoices. A pending invoice is refreshed from the node, so this is
    /// the call to poll while waiting for payment.
    /// </summary>
    [HttpGet("invoices/{paymentHash}")]
    [Authorize(Policy = ApiKeyPolicies.InvoicesRead)]
    [ProducesResponseType(typeof(LightningInvoiceStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetInvoice(string paymentHash, CancellationToken cancellationToken)
    {
        if (MerchantId() is not { } merchantId)
            return NoMerchant();

        if (!PaymentHashPattern().IsMatch(paymentHash))
            return Invalid("paymentHash must be 64 hexadecimal characters.");

        var result = await invoiceService.GetAsync(merchantId, paymentHash, cancellationToken);
        return result.Outcome switch
        {
            InvoiceReadOutcome.Found when result.Invoice is { } invoice => Ok(LightningInvoiceStatusResponse.From(invoice)),
            InvoiceReadOutcome.NotFound => NotFound(),
            InvoiceReadOutcome.BackendUnavailable => Unavailable(result.Message),
            _ => NodeFailure(result.Message),
        };
    }

    /// <summary>
    /// Lists the merchant's invoices, newest first, as last recorded: a pending invoice is not
    /// refreshed here, so read it individually for its current state. Pass a page's
    /// <c>nextCursor</c> as <c>before</c> for the next page.
    /// </summary>
    /// <param name="status"><c>pending</c>, <c>paid</c> or <c>expired</c>.</param>
    /// <param name="externalId">Only invoices with this reference.</param>
    /// <param name="limit">Page size, 1 to 100 (default 50).</param>
    /// <param name="before">The previous page's <c>nextCursor</c>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    [HttpGet("invoices")]
    [Authorize(Policy = ApiKeyPolicies.InvoicesRead)]
    [ProducesResponseType(typeof(LightningInvoicePage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ListInvoices(
        [FromQuery] string? status,
        [FromQuery, StringLength(100)] string? externalId,
        [FromQuery, Range(1, LightningInvoiceService.MaxPageSize)] int? limit,
        [FromQuery, Range(1, long.MaxValue)] long? before,
        CancellationToken cancellationToken)
    {
        if (MerchantId() is not { } merchantId)
            return NoMerchant();

        LightningInvoiceStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!LightningInvoiceStatusResponse.TryParseStatus(status, out var parsed))
                return Invalid("status must be pending, paid or expired.");
            statusFilter = parsed;
        }

        var (items, nextCursor) = await invoiceService.ListAsync(merchantId,
            new LightningInvoiceQuery(statusFilter, externalId, limit, before), cancellationToken);

        return Ok(new LightningInvoicePage
        {
            Items = items.Select(LightningInvoiceStatusResponse.From).ToList(),
            NextCursor = nextCursor,
        });
    }

    /// <summary>The merchant the calling key was issued for; null for keys that belong to none (e.g. master keys).</summary>
    private int? MerchantId() => ClaimAsInt(ApiKeyDefaults.MerchantIdClaim);

    private int? KeyId() => ClaimAsInt(ApiKeyDefaults.KeyIdClaim);

    private int? ClaimAsInt(string type) =>
        int.TryParse(User.FindFirst(type)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private ObjectResult NoMerchant() =>
        Problem(title: "No merchant", detail: "Invoices belong to a merchant, and this key was not issued for one.",
            statusCode: StatusCodes.Status403Forbidden);

    private ObjectResult Invalid(string detail) =>
        Problem(title: "Invalid invoice request", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult Unavailable(string? detail) =>
        Problem(title: "Lightning payments unavailable",
            detail: detail ?? "No Lightning backend is available for this merchant.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private ObjectResult NodeFailure(string? detail) =>
        Problem(title: "Lightning node error", detail: detail ?? "The Lightning node could not complete the request.",
            statusCode: StatusCodes.Status502BadGateway);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex PaymentHashPattern();

    [GeneratedRegex(@"^[\x21-\x7E]{1,100}$")]
    private static partial Regex IdempotencyKeyPattern();
}
