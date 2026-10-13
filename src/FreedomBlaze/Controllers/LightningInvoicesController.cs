using System.Globalization;
using System.Text.RegularExpressions;
using FreedomBlaze.Authentication;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Phoenixd.NET.Exceptions;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Fiat-priced Lightning invoices for integrators (e.g. a store checkout). Unlike the operator API at
/// <c>/api/payment-manager</c>, which needs a master key because it can move funds, these endpoints
/// can only <b>receive</b>: they quote an amount in satoshis, create the invoice on the node in the
/// same request, and report its status. Any active API key may use them.
/// <para>
/// Every invoice is tagged with the issuing key (its phoenixd <c>externalId</c> starts with
/// <c>key{id}:</c>), and a status read for an invoice another key issued answers exactly like an
/// unknown one, so keys cannot observe each other's payments.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/lightning")]
[Tags("Lightning Invoices")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]
[EnableRateLimiting(RateLimitPolicies.PerApiKey)]
[Produces("application/json")]
public sealed partial class LightningInvoicesController(
    ICurrencyConversionService conversionService,
    IOptions<LightningInvoiceOptions> options,
    ILogger<LightningInvoicesController> logger,
    IPaymentService? payments = null,
    INodeService? node = null) : ControllerBase
{
    private static readonly TimeSpan NodeProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly LightningInvoiceOptions _settings = options.Value;

    /// <summary>Reports whether invoices can be issued right now, so a checkout can hide the option when not.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(LightningGatewayStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LightningGatewayStatus>> GetStatus(CancellationToken cancellationToken)
    {
        if (payments is null || node is null)
            return Ok(new LightningGatewayStatus { Enabled = false, Reachable = false });

        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(NodeProbeTimeout);
        try
        {
            await node.GetNodeInfo(probe.Token);
            return Ok(new LightningGatewayStatus { Enabled = true, Reachable = true });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is PhoenixdApiException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "phoenixd did not answer the gateway status probe.");
            return Ok(new LightningGatewayStatus { Enabled = true, Reachable = false });
        }
    }

    /// <summary>
    /// Quotes the amount in satoshis (rounded up) and creates a BOLT11 invoice for it. The quote
    /// holds for the invoice's lifetime.
    /// </summary>
    [HttpPost("invoices")]
    [ProducesResponseType(typeof(LightningInvoiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateInvoice(CreateLightningInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (payments is null)
            return Unavailable();

        if (OwnerPrefix() is not { } owner)
            return Forbid(ApiKeyDefaults.Scheme);

        if (request.Amount <= 0)
            return Invalid("Amount must be greater than zero.");

        if (!TryValidateWebhook(request.WebhookUrl, out var webhookError))
            return Invalid(webhookError);

        SatsQuote quote;
        try
        {
            quote = await conversionService.QuoteSatsAsync(request.Amount, request.Currency,
                new SatsQuoteRules(_settings.MinimumRateSources, _settings.MaxFiatRateAge), cancellationToken);
        }
        catch (ConversionException ex) when (ex.IsClientError)
        {
            return Invalid(ex.Message);
        }
        catch (ConversionException ex)
        {
            logger.LogWarning("Refused to quote {Amount} {Currency}: {Reason}", request.Amount, request.Currency, ex.Message);
            return Problem(title: "Price unavailable", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (quote.AmountSat > _settings.MaxAmountSat)
            return Invalid($"The amount prices at {quote.AmountSat} sats, above the {_settings.MaxAmountSat} sat limit.");

        var expirySeconds = request.ExpirySeconds ?? _settings.DefaultExpirySeconds;
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? $"Payment of {quote.Amount.ToString("0.########", CultureInfo.InvariantCulture)} {quote.Currency}"
            : request.Description.Trim();

        Invoice invoice;
        try
        {
            invoice = await payments.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                AmountSat = quote.AmountSat,
                Description = description,
                ExternalId = owner + request.ExternalId,
                ExpirySeconds = expirySeconds,
                WebhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is PhoenixdApiException or HttpRequestException)
        {
            logger.LogError(ex, "phoenixd could not create a {AmountSat} sat invoice.", quote.AmountSat);
            return NodeFailure();
        }

        logger.LogInformation("Issued {AmountSat} sat invoice {PaymentHash} for {Amount} {Currency} ({Owner}{ExternalId}).",
            quote.AmountSat, invoice.PaymentHash, quote.Amount, quote.Currency, owner, request.ExternalId);

        return CreatedAtAction(nameof(GetInvoice), new { paymentHash = invoice.PaymentHash }, new LightningInvoiceResponse
        {
            PaymentHash = invoice.PaymentHash,
            Bolt11 = invoice.Serialized,
            AmountSat = quote.AmountSat,
            Amount = quote.Amount,
            Currency = quote.Currency,
            BitcoinPrice = quote.BitcoinPrice,
            BitcoinPriceUsd = quote.BitcoinPriceUsd,
            RateSources = quote.RateSources,
            FiatRateTimestamp = quote.FiatRateTimestamp,
            QuotedAt = quote.QuotedAt,
            ExpiresAt = quote.QuotedAt.AddSeconds(expirySeconds),
            ExternalId = request.ExternalId,
        });
    }

    /// <summary>Reads an invoice's live state from the node. Only the key that issued it can see it.</summary>
    [HttpGet("invoices/{paymentHash}")]
    [ProducesResponseType(typeof(LightningInvoiceStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetInvoice(string paymentHash, CancellationToken cancellationToken)
    {
        if (payments is null)
            return Unavailable();

        if (OwnerPrefix() is not { } owner)
            return Forbid(ApiKeyDefaults.Scheme);

        if (!PaymentHashPattern().IsMatch(paymentHash))
            return Invalid("paymentHash must be 64 hexadecimal characters.");

        PaymentInfo? payment;
        try
        {
            payment = await payments.FindIncomingPaymentAsync(paymentHash.ToLowerInvariant(), cancellationToken);
        }
        catch (Exception ex) when (ex is PhoenixdApiException or HttpRequestException)
        {
            logger.LogError(ex, "phoenixd could not look up invoice {PaymentHash}.", paymentHash);
            return NodeFailure();
        }

        // Another key's invoice answers exactly like a missing one.
        if (payment?.ExternalId is not { } externalId || !externalId.StartsWith(owner, StringComparison.Ordinal))
            return NotFound();

        return Ok(new LightningInvoiceStatusResponse
        {
            PaymentHash = payment.PaymentHash,
            ExternalId = externalId.Length > owner.Length ? externalId[owner.Length..] : null,
            Status = payment.IsPaid ? LightningInvoiceStatusResponse.Paid
                : payment.IsExpired ? LightningInvoiceStatusResponse.Expired
                : LightningInvoiceStatusResponse.Pending,
            IsPaid = payment.IsPaid,
            RequestedSat = payment.RequestedSat,
            ReceivedSat = payment.ReceivedSat,
            // phoenixd reports incoming fees in millisatoshis.
            FeeSat = (long)Math.Ceiling(payment.Fees / 1000m),
            Bolt11 = payment.Invoice,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(payment.CreatedAt),
            ExpiresAt = payment.ExpiresAt is { } expiresAt ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) : null,
            CompletedAt = payment.CompletedAt is { } completedAt ? DateTimeOffset.FromUnixTimeMilliseconds(completedAt) : null,
        });
    }

    /// <summary>The <c>externalId</c> prefix that marks invoices issued by the calling key.</summary>
    private string? OwnerPrefix() =>
        User.FindFirst(ApiKeyDefaults.KeyIdClaim)?.Value is { Length: > 0 } keyId ? $"key{keyId}:" : null;

    private bool TryValidateWebhook(string? url, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "webhookUrl must be an absolute HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "webhookUrl must not embed credentials.";
            return false;
        }

        if (_settings.AllowedWebhookHosts.Length > 0
            && !_settings.AllowedWebhookHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            error = $"webhookUrl host '{uri.Host}' is not allowed.";
            return false;
        }

        return true;
    }

    private ObjectResult Invalid(string detail) =>
        Problem(title: "Invalid invoice request", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult Unavailable() =>
        Problem(title: "Lightning payments unavailable", detail: "phoenixd is not configured on this server.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private ObjectResult NodeFailure() =>
        Problem(title: "Lightning node error", detail: "The Lightning node could not complete the request.",
            statusCode: StatusCodes.Status502BadGateway);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex PaymentHashPattern();
}
