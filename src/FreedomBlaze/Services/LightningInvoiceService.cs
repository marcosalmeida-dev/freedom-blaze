using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Default <see cref="ILightningInvoiceService"/>. Every invoice is recorded before the merchant's node is
/// asked to issue it: with an <c>Idempotency-Key</c>, the unique (merchant, key) index makes the first
/// request the only one that can reach the node, and retries are answered from the record.
/// <para>
/// The node is not taken on trust. Each backend pins its node's public key, an issued invoice is handed
/// out only when its signature, payment hash, amount and lifetime check out, and a payment counts only
/// when the node shows the preimage that hashes to the invoice's payment hash.
/// </para>
/// <para>
/// A pending invoice is also confirmed in the background (<see cref="LightningInvoiceSweeper"/>), and every
/// settlement queues the merchant's webhooks in the same transaction (<see cref="LightningInvoiceRefresher"/>).
/// </para>
/// </summary>
public sealed class LightningInvoiceService(
    ICurrencyConversionService conversionService,
    IRepository<LightningInvoice> invoices,
    IRepository<LightningBackend> backends,
    ILightningBackendFactory backendFactory,
    LightningInvoiceRefresher refresher,
    IOptions<LightningInvoiceOptions> options,
    TimeProvider timeProvider,
    ILogger<LightningInvoiceService> logger) : ILightningInvoiceService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>
    /// A reservation older than this was abandoned: its request died between reserving the key and
    /// recording the issued invoice. The key is freed for a retry.
    /// </summary>
    internal static readonly TimeSpan AbandonedReservationAge = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StatusProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly LightningInvoiceOptions _settings = options.Value;

    public async Task<LightningGatewayStatus> GetStatusAsync(int merchantId, CancellationToken cancellationToken)
    {
        var backend = await ActiveBackendAsync(merchantId, cancellationToken);
        if (backend is null)
            return new LightningGatewayStatus { Enabled = false, Reachable = false };

        if (!TryConnect(backend, out var node, out _))
            return new LightningGatewayStatus { Enabled = true, Reachable = false };

        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(StatusProbeTimeout);
        try
        {
            var info = await node.GetInfoAsync(probe.Token);
            var samePinnedNode = backend.NodeId is null || string.Equals(info.NodeId, backend.NodeId, StringComparison.OrdinalIgnoreCase);
            if (!samePinnedNode)
                logger.LogError("Backend {BackendId} of merchant {MerchantId} now answers as node {NodeId}, not the pinned {PinnedNodeId}.",
                    backend.Id, merchantId, info.NodeId, backend.NodeId);

            return new LightningGatewayStatus { Enabled = true, Reachable = samePinnedNode };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is LightningBackendException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Backend {BackendId} of merchant {MerchantId} did not answer the status probe.", backend.Id, merchantId);
            return new LightningGatewayStatus { Enabled = true, Reachable = false };
        }
    }

    public async Task<InvoiceCreationResult> CreateAsync(
        InvoiceIssuer issuer, CreateLightningInvoiceRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0)
            return Invalid("Amount must be greater than zero.");

        if (!TryValidateWebhook(request.WebhookUrl, out var webhookError))
            return Invalid(webhookError);

        var requestHash = HashRequest(request);
        if (idempotencyKey is not null
            && await ResolvePriorAsync(issuer.MerchantId, idempotencyKey, requestHash, reclaimAbandoned: true, cancellationToken) is { } prior)
            return prior;

        var backend = await ActiveBackendAsync(issuer.MerchantId, cancellationToken);
        if (backend is null)
            return Unavailable("No Lightning backend is configured for this merchant.");

        if (!TryConnect(backend, out var node, out var problem))
            return Unavailable(problem);

        if (backend.NodeId is null && await PinAsync(backend, node, cancellationToken) is { } pinFailure)
            return pinFailure;

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
            BackendId = backend.Id,
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

        BackendInvoice issued;
        try
        {
            issued = await node.CreateInvoiceAsync(new BackendInvoiceRequest(
                invoice.AmountSat,
                invoice.Description,
                // The node's own label: whose invoice it is, then the merchant's reference.
                $"m{issuer.MerchantId}:{request.ExternalId}",
                expirySeconds,
                invoice.WebhookUrl), cancellationToken);
        }
        catch (Exception ex) when (ex is LightningBackendException or OperationCanceledException)
        {
            // Whatever the node may have issued was never handed out, so the key must stay usable for a retry.
            await ReleaseAsync(invoice);
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogError(ex, "Backend {BackendId} could not create a {AmountSat} sat invoice for merchant {MerchantId}.",
                backend.Id, invoice.AmountSat, issuer.MerchantId);
            return new InvoiceCreationResult(InvoiceCreationOutcome.BackendFailure, Message: ex.Message);
        }

        if (VerifyIssued(issued, invoice.AmountSat, expirySeconds, backend.NodeId!) is { } rejection)
        {
            await ReleaseAsync(invoice);
            logger.LogError("Refused the invoice backend {BackendId} issued for merchant {MerchantId}: {Reason}. Invoice: {Bolt11}",
                backend.Id, issuer.MerchantId, rejection, issued.Bolt11);
            return new InvoiceCreationResult(InvoiceCreationOutcome.BackendFailure,
                Message: $"The node's invoice failed verification: {rejection}.");
        }

        invoice.PaymentHash = issued.PaymentHash;
        invoice.Bolt11 = issued.Bolt11;
        invoice.Status = LightningInvoiceStatus.Pending;
        invoice.NextCheckAtUtc = LightningInvoiceSchedule.FirstCheck(invoice.CreatedAtUtc);

        // Recorded even if the caller has gone: the node issued it, and a retry must find it.
        await invoices.UpdateAsync(invoice, CancellationToken.None);

        logger.LogInformation(
            "Issued {AmountSat} sat invoice {PaymentHash} for {Amount} {Currency} (merchant {MerchantId}, backend {BackendId}, ref {ExternalId}).",
            invoice.AmountSat, invoice.PaymentHash, invoice.Amount, invoice.Currency, issuer.MerchantId, backend.Id, invoice.ExternalId);
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

        // Read from the backend that issued it, even if the merchant has moved to another one since.
        var backend = await backends.GetByIdAsync(invoice.BackendId, cancellationToken);
        if (backend is null)
            return new InvoiceReadResult(InvoiceReadOutcome.BackendUnavailable,
                Message: "The backend that issued this invoice no longer exists.");

        // Recorded the same way as by the background sweep, webhooks included, so whichever asks first
        // records the change and the other finds it recorded.
        var refreshed = await refresher.RefreshAsync(invoice, backend, cancellationToken);
        return refreshed.Outcome switch
        {
            InvoiceRefreshOutcome.BackendUnavailable => new InvoiceReadResult(InvoiceReadOutcome.BackendUnavailable, Message: refreshed.Message),
            InvoiceRefreshOutcome.BackendFailure or InvoiceRefreshOutcome.Unproven =>
                new InvoiceReadResult(InvoiceReadOutcome.BackendFailure, Message: refreshed.Message),
            _ => new InvoiceReadResult(InvoiceReadOutcome.Found, refreshed.Invoice),
        };
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
    /// Why an invoice the node issued must not be handed out, or null when it checks out: it must carry the
    /// pinned node's signature, the payment hash the node reported, exactly the quoted amount, and no longer
    /// a lifetime than the quote holds for.
    /// </summary>
    internal static string? VerifyIssued(BackendInvoice issued, long amountSat, int expirySeconds, string pinnedNodeId)
    {
        if (!Bolt11Invoice.TryParse(issued.Bolt11, out var bolt11, out var error))
            return $"it is not a valid BOLT11 invoice ({error})";
        if (!string.Equals(bolt11.PayeeNodeId, pinnedNodeId, StringComparison.OrdinalIgnoreCase))
            return $"it is signed by node {bolt11.PayeeNodeId}, not the pinned {pinnedNodeId}";
        if (!string.Equals(bolt11.PaymentHash, issued.PaymentHash, StringComparison.OrdinalIgnoreCase))
            return "its payment hash is not the one the node reported";
        if (bolt11.AmountMsat != amountSat * 1000)
            return $"it asks for {bolt11.AmountMsat?.ToString(CultureInfo.InvariantCulture) ?? "any amount"} msat instead of {amountSat * 1000}";
        if (bolt11.ExpirySeconds > expirySeconds)
            return $"it stays payable for {bolt11.ExpirySeconds} s instead of {expirySeconds} s";
        return null;
    }

    private Task<LightningBackend?> ActiveBackendAsync(int merchantId, CancellationToken cancellationToken) =>
        backends.FirstOrDefaultAsync(b => b.MerchantId == merchantId && b.IsActive, cancellationToken);

    private bool TryConnect(LightningBackend backend, [NotNullWhen(true)] out ILightningBackend? node, [NotNullWhen(false)] out string? problem)
    {
        try
        {
            node = backendFactory.Create(backend);
            problem = null;
            return true;
        }
        catch (LightningBackendUnavailableException ex)
        {
            logger.LogError(ex, "Backend {BackendId} of merchant {MerchantId} cannot be used.", backend.Id, backend.MerchantId);
            node = null;
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Pins the node a backend reaches on its first use, for backends created before pinning existed. Only
    /// fills an empty pin, so it never overwrites one an operator set.
    /// </summary>
    private async Task<InvoiceCreationResult?> PinAsync(LightningBackend backend, ILightningBackend node, CancellationToken cancellationToken)
    {
        BackendNodeInfo info;
        try
        {
            info = await node.GetInfoAsync(cancellationToken);
        }
        catch (LightningBackendException ex)
        {
            logger.LogError(ex, "Backend {BackendId} did not describe its node, so it cannot be pinned.", backend.Id);
            return new InvoiceCreationResult(InvoiceCreationOutcome.BackendFailure, Message: ex.Message);
        }

        await backends.QueryAsync((all, token) => all
            .Where(b => b.Id == backend.Id && b.NodeId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.NodeId, info.NodeId).SetProperty(b => b.Chain, info.Chain), token),
            CancellationToken.None);

        backend.NodeId = info.NodeId;
        backend.Chain = info.Chain;
        logger.LogInformation("Pinned node {NodeId} ({Chain}) for backend {BackendId} of merchant {MerchantId}.",
            info.NodeId, info.Chain, backend.Id, backend.MerchantId);
        return null;
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

    private static InvoiceCreationResult Unavailable(string message) =>
        new(InvoiceCreationOutcome.BackendUnavailable, Message: message);
}
