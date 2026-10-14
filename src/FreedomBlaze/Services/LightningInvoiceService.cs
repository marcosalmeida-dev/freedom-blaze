using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phoenixd.NET.Exceptions;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Services;

/// <summary>
/// Default <see cref="ILightningInvoiceService"/>. Every invoice is recorded before the node is asked to
/// issue it: with an <c>Idempotency-Key</c>, the unique (merchant, key) index makes the first request
/// the only one that can reach the node, and retries are answered from the record.
/// <para>
/// The record is the gateway's: ownership, listing and history. The node remains the source of truth
/// for whether a pending invoice has been paid, so a read refreshes it from there.
/// </para>
/// </summary>
public sealed class LightningInvoiceService(
    ICurrencyConversionService conversionService,
    IRepository<LightningInvoice> invoices,
    IOptions<LightningInvoiceOptions> options,
    TimeProvider timeProvider,
    ILogger<LightningInvoiceService> logger,
    IPaymentService? payments = null) : ILightningInvoiceService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>
    /// A reservation older than this was abandoned: its request died between reserving the key and
    /// recording the issued invoice. The key is freed for a retry.
    /// </summary>
    internal static readonly TimeSpan AbandonedReservationAge = TimeSpan.FromMinutes(2);

    private readonly LightningInvoiceOptions _settings = options.Value;

    public bool IsConfigured => payments is not null;

    public async Task<InvoiceCreationResult> CreateAsync(
        InvoiceIssuer issuer, CreateLightningInvoiceRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (payments is null)
            return new InvoiceCreationResult(InvoiceCreationOutcome.NodeUnavailable);

        if (request.Amount <= 0)
            return Invalid("Amount must be greater than zero.");

        if (!TryValidateWebhook(request.WebhookUrl, out var webhookError))
            return Invalid(webhookError);

        var requestHash = HashRequest(request);
        if (idempotencyKey is not null
            && await ResolvePriorAsync(issuer.MerchantId, idempotencyKey, requestHash, reclaimAbandoned: true, cancellationToken) is { } prior)
            return prior;

        SatsQuote quote;
        try
        {
            quote = await conversionService.QuoteSatsAsync(request.Amount, request.Currency ?? string.Empty,
                new SatsQuoteRules(_settings.MinimumRateSources, _settings.MaxFiatRateAge), cancellationToken);
        }
        catch (ConversionException ex) when (ex.IsClientError)
        {
            return Invalid(ex.Message);
        }
        catch (ConversionException ex)
        {
            logger.LogWarning("Refused to quote {Amount} {Currency}: {Reason}", request.Amount, request.Currency, ex.Message);
            return new InvoiceCreationResult(InvoiceCreationOutcome.PriceUnavailable, Message: ex.Message);
        }

        if (quote.AmountSat > _settings.MaxAmountSat)
            return Invalid($"The amount prices at {quote.AmountSat} sats, above the {_settings.MaxAmountSat} sat limit.");

        var expirySeconds = request.ExpirySeconds ?? _settings.DefaultExpirySeconds;
        var invoice = new LightningInvoice
        {
            MerchantId = issuer.MerchantId,
            ApiKeyId = issuer.ApiKeyId,
            Status = LightningInvoiceStatus.Creating,
            ExternalId = request.ExternalId,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? $"Payment of {quote.Amount.ToString("0.########", CultureInfo.InvariantCulture)} {quote.Currency}"
                : request.Description.Trim(),
            Amount = quote.Amount,
            Currency = quote.Currency,
            AmountSat = quote.AmountSat,
            BitcoinPrice = quote.BitcoinPrice,
            BitcoinPriceUsd = quote.BitcoinPriceUsd,
            RateSources = quote.RateSources,
            FiatRateTimestamp = quote.FiatRateTimestamp,
            QuotedAtUtc = quote.QuotedAt,
            ExpiresAtUtc = quote.QuotedAt.AddSeconds(expirySeconds),
            CreatedAtUtc = timeProvider.GetUtcNow(),
            WebhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };

        try
        {
            await invoices.AddAsync(invoice, cancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // A concurrent request with the same key reserved it first; answer as that one will be.
            if (await ResolvePriorAsync(issuer.MerchantId, idempotencyKey, requestHash, reclaimAbandoned: false, cancellationToken) is { } concurrent)
                return concurrent;

            throw; // Nothing holds the key, so the insert failed for another reason.
        }

        Invoice issued;
        try
        {
            issued = await payments.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                AmountSat = invoice.AmountSat,
                Description = invoice.Description,
                // The node's own label: whose invoice it is, then the merchant's reference.
                ExternalId = $"m{issuer.MerchantId}:{request.ExternalId}",
                ExpirySeconds = expirySeconds,
                WebhookUrl = invoice.WebhookUrl,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is PhoenixdApiException or HttpRequestException or OperationCanceledException)
        {
            // Whatever the node may have issued was never handed out, so the key must stay usable for a retry.
            await ReleaseAsync(invoice);
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogError(ex, "phoenixd could not create a {AmountSat} sat invoice for merchant {MerchantId}.",
                invoice.AmountSat, issuer.MerchantId);
            return new InvoiceCreationResult(InvoiceCreationOutcome.NodeFailure);
        }

        invoice.PaymentHash = issued.PaymentHash.ToLowerInvariant();
        invoice.Bolt11 = issued.Serialized;
        invoice.Status = LightningInvoiceStatus.Pending;

        // Recorded even if the caller has gone: the node issued it, and a retry must find it.
        await invoices.UpdateAsync(invoice, CancellationToken.None);

        logger.LogInformation(
            "Issued {AmountSat} sat invoice {PaymentHash} for {Amount} {Currency} (merchant {MerchantId}, ref {ExternalId}).",
            invoice.AmountSat, invoice.PaymentHash, invoice.Amount, invoice.Currency, issuer.MerchantId, invoice.ExternalId);
        return new InvoiceCreationResult(InvoiceCreationOutcome.Created, invoice);
    }

    public async Task<InvoiceReadResult> GetAsync(int merchantId, string paymentHash, CancellationToken cancellationToken)
    {
        var hash = paymentHash.ToLowerInvariant();
        var invoice = await invoices.FirstOrDefaultAsync(i => i.MerchantId == merchantId && i.PaymentHash == hash, cancellationToken);
        if (invoice is null)
            return new InvoiceReadResult(InvoiceReadOutcome.NotFound);

        // Paid and expired are final; only a pending invoice can still change.
        if (invoice.Status != LightningInvoiceStatus.Pending)
            return new InvoiceReadResult(InvoiceReadOutcome.Found, invoice);

        if (payments is null)
            return new InvoiceReadResult(InvoiceReadOutcome.NodeUnavailable);

        PaymentInfo? payment;
        try
        {
            payment = await payments.FindIncomingPaymentAsync(hash, cancellationToken);
        }
        catch (Exception ex) when (ex is PhoenixdApiException or HttpRequestException)
        {
            logger.LogError(ex, "phoenixd could not look up invoice {PaymentHash}.", hash);
            return new InvoiceReadResult(InvoiceReadOutcome.NodeFailure);
        }

        if (payment is null)
        {
            logger.LogError("The node no longer knows invoice {PaymentHash} issued for merchant {MerchantId}.", hash, merchantId);
            return new InvoiceReadResult(InvoiceReadOutcome.Found, invoice);
        }

        if (ApplyNodeState(invoice, payment))
            await invoices.UpdateAsync(invoice, CancellationToken.None);

        return new InvoiceReadResult(InvoiceReadOutcome.Found, invoice);
    }

    public async Task<(IReadOnlyList<LightningInvoice> Items, long? NextCursor)> ListAsync(
        int merchantId, LightningInvoiceQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var page = await invoices.QueryAsync(async (all, token) =>
        {
            var matching = all.Where(i => i.MerchantId == merchantId && i.Status != LightningInvoiceStatus.Creating);
            if (query.Status is { } status)
                matching = matching.Where(i => i.Status == status);
            if (!string.IsNullOrEmpty(query.ExternalId))
                matching = matching.Where(i => i.ExternalId == query.ExternalId);
            if (query.Before is { } before)
                matching = matching.Where(i => i.Id < before);

            // One extra row says whether another page exists without a separate count.
            return await matching.OrderByDescending(i => i.Id).Take(limit + 1).ToListAsync(token);
        }, cancellationToken);

        if (page.Count <= limit)
            return (page, null);

        var items = page.Take(limit).ToList();
        return (items, items[^1].Id);
    }

    /// <summary>
    /// What a request under an already-used <c>Idempotency-Key</c> gets: the original invoice, a refusal
    /// when the key was used for something else, or "in progress" while the first request still runs.
    /// Null when the key is free — including once an abandoned reservation has been cleared.
    /// </summary>
    private async Task<InvoiceCreationResult?> ResolvePriorAsync(
        int merchantId, string idempotencyKey, string requestHash, bool reclaimAbandoned, CancellationToken cancellationToken)
    {
        var prior = await invoices.FirstOrDefaultAsync(
            i => i.MerchantId == merchantId && i.IdempotencyKey == idempotencyKey, cancellationToken);
        if (prior is null)
            return null;

        if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
            return new InvoiceCreationResult(InvoiceCreationOutcome.IdempotencyMismatch,
                Message: "This Idempotency-Key was already used for a different request.");

        if (prior.Status != LightningInvoiceStatus.Creating)
            return new InvoiceCreationResult(InvoiceCreationOutcome.Replayed, prior);

        if (!reclaimAbandoned || timeProvider.GetUtcNow() - prior.CreatedAtUtc < AbandonedReservationAge)
            return new InvoiceCreationResult(InvoiceCreationOutcome.IdempotencyInProgress);

        logger.LogWarning("Reclaiming idempotency key {IdempotencyKey} of merchant {MerchantId}: its request was abandoned at {CreatedAt}.",
            idempotencyKey, merchantId, prior.CreatedAtUtc);
        await ReleaseAsync(prior);
        return null;
    }

    private async Task ReleaseAsync(LightningInvoice reservation)
    {
        try
        {
            await invoices.RemoveAsync(reservation, CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent retry already cleared it.
        }
    }

    /// <summary>Copies the node's verdict onto the record. True when anything changed.</summary>
    private bool ApplyNodeState(LightningInvoice invoice, PaymentInfo payment)
    {
        if (payment.IsPaid)
        {
            if (payment.RequestedSat is { } requested && requested != invoice.AmountSat)
                logger.LogError("Invoice {PaymentHash}: the node requested {RequestedSat} sats, the gateway recorded {AmountSat}.",
                    invoice.PaymentHash, requested, invoice.AmountSat);

            invoice.Status = LightningInvoiceStatus.Paid;
            invoice.PaidAtUtc = payment.CompletedAt is { } completedAt
                ? DateTimeOffset.FromUnixTimeMilliseconds(completedAt)
                : timeProvider.GetUtcNow();
            invoice.ReceivedSat = payment.ReceivedSat;
            invoice.FeeSat = (long)Math.Ceiling(payment.Fees / 1000m); // phoenixd reports millisatoshis
            return true;
        }

        if (payment.IsExpired)
        {
            invoice.Status = LightningInvoiceStatus.Expired;
            return true;
        }

        return false;
    }

    /// <summary>
    /// SHA-256 of the request's meaning, so a retry with the same <c>Idempotency-Key</c> and the same
    /// request is recognised even when the JSON is spelled differently (40 vs 40.00, BRL vs brl).
    /// </summary>
    internal static string HashRequest(CreateLightningInvoiceRequest request)
    {
        var canonical = string.Join('\u001F',
            request.Amount.ToString("G29", CultureInfo.InvariantCulture),
            request.Currency?.Trim().ToUpperInvariant() ?? string.Empty,
            request.ExternalId ?? string.Empty,
            request.Description?.Trim() ?? string.Empty,
            request.ExpirySeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.WebhookUrl?.Trim() ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

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

    private static InvoiceCreationResult Invalid(string message) =>
        new(InvoiceCreationOutcome.Invalid, Message: message);
}
