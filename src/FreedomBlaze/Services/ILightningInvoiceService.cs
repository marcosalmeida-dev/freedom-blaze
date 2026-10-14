using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>Who is issuing an invoice: the merchant it will belong to, and the key that asked.</summary>
public sealed record InvoiceIssuer(int MerchantId, int? ApiKeyId);

public enum InvoiceCreationOutcome
{
    /// <summary>A new invoice was issued.</summary>
    Created,

    /// <summary>The <c>Idempotency-Key</c> was seen before with the same request: the original invoice.</summary>
    Replayed,

    /// <summary>The request cannot be priced or issued as asked (bad amount, currency, webhook…).</summary>
    Invalid,

    /// <summary>No trustworthy price right now (too few exchanges, stale or placeholder fiat rate).</summary>
    PriceUnavailable,

    /// <summary>No node is configured on this server.</summary>
    NodeUnavailable,

    /// <summary>The node refused or could not be reached.</summary>
    NodeFailure,

    /// <summary>The <c>Idempotency-Key</c> was already used for a different request.</summary>
    IdempotencyMismatch,

    /// <summary>A request with the same <c>Idempotency-Key</c> is still being processed.</summary>
    IdempotencyInProgress,
}

public sealed record InvoiceCreationResult(
    InvoiceCreationOutcome Outcome, LightningInvoice? Invoice = null, string? Message = null);

public enum InvoiceReadOutcome
{
    Found,

    /// <summary>No such invoice for this merchant — including another merchant's invoice.</summary>
    NotFound,

    NodeUnavailable,
    NodeFailure,
}

public sealed record InvoiceReadResult(InvoiceReadOutcome Outcome, LightningInvoice? Invoice = null);

/// <summary>Filters for listing a merchant's invoices; <see cref="Before"/> is the paging cursor.</summary>
public sealed record LightningInvoiceQuery(
    LightningInvoiceStatus? Status = null, string? ExternalId = null, int? Limit = null, long? Before = null);

/// <summary>
/// Issues, reads and lists merchants' Lightning invoices. Every operation is scoped to one merchant:
/// another merchant's invoice is indistinguishable from one that does not exist.
/// </summary>
public interface ILightningInvoiceService
{
    /// <summary>True when a node is configured; without one nothing can be issued or checked.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Prices the request and issues the invoice. With an <paramref name="idempotencyKey"/>, a repeat of
    /// the same request returns the original invoice instead of issuing another.
    /// </summary>
    Task<InvoiceCreationResult> CreateAsync(
        InvoiceIssuer issuer, CreateLightningInvoiceRequest request, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The merchant's invoice, refreshed from the node while it is still pending.</summary>
    Task<InvoiceReadResult> GetAsync(int merchantId, string paymentHash, CancellationToken cancellationToken);

    /// <summary>
    /// The merchant's invoices, newest first, as last recorded (not refreshed from the node).
    /// <c>NextCursor</c> is null on the last page.
    /// </summary>
    Task<(IReadOnlyList<LightningInvoice> Items, long? NextCursor)> ListAsync(
        int merchantId, LightningInvoiceQuery query, CancellationToken cancellationToken);
}
