namespace FreedomBlaze.Data.Entities;

/// <summary>Lifecycle of a <see cref="LightningInvoice"/>.</summary>
public enum LightningInvoiceStatus
{
    /// <summary>
    /// Reserved, but the node has not issued it yet. Only exists while a creation request is in
    /// flight (or after one died mid-way); never shown to merchants.
    /// </summary>
    Creating = 0,

    /// <summary>Issued and awaiting payment.</summary>
    Pending = 1,

    /// <summary>Paid. Final.</summary>
    Paid = 2,

    /// <summary>Expired unpaid. Final: an expired Lightning invoice cannot be paid.</summary>
    Expired = 3,
}

/// <summary>
/// A Lightning invoice the gateway issued for a merchant, with the quote that priced it. The gateway's
/// own record: ownership, listing, idempotency and history live here, while the node stays the source
/// of truth for whether a pending invoice has been paid.
/// </summary>
public class LightningInvoice
{
    public long Id { get; set; }

    /// <summary>The merchant the invoice belongs to; only its keys can see it.</summary>
    public int MerchantId { get; set; }

    /// <summary>The key that issued it, for auditing.</summary>
    public int? ApiKeyId { get; set; }

    public LightningInvoiceStatus Status { get; set; }

    /// <summary>The node's payment hash (64 hex). Null only while <see cref="LightningInvoiceStatus.Creating"/>.</summary>
    public string? PaymentHash { get; set; }

    /// <summary>The BOLT11 payment request. Null only while <see cref="LightningInvoiceStatus.Creating"/>.</summary>
    public string? Bolt11 { get; set; }

    /// <summary>The merchant's own reference (e.g. an order id).</summary>
    public string? ExternalId { get; set; }

    /// <summary>Text the payer's wallet shows.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The amount priced, in <see cref="Currency"/>.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Satoshis the invoice asks for (the amount, rounded up).</summary>
    public long AmountSat { get; set; }

    /// <summary>Price of one bitcoin in <see cref="Currency"/> that the quote used.</summary>
    public decimal BitcoinPrice { get; set; }

    /// <summary>The aggregated BTC/USD price, or null for invoices priced in BTC/SATS.</summary>
    public decimal? BitcoinPriceUsd { get; set; }

    /// <summary>How many exchanges the BTC/USD price came from.</summary>
    public int RateSources { get; set; }

    /// <summary>When the fiat rate was published; null when no fiat conversion was involved.</summary>
    public DateTimeOffset? FiatRateTimestamp { get; set; }

    public DateTimeOffset QuotedAtUtc { get; set; }

    /// <summary>When the invoice stops being payable (measured from the quote, so never later than the node's).</summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the node reported the payment complete.</summary>
    public DateTimeOffset? PaidAtUtc { get; set; }

    /// <summary>Satoshis the node received.</summary>
    public long? ReceivedSat { get; set; }

    /// <summary>Fees the node paid on receipt (e.g. inbound liquidity), in satoshis.</summary>
    public long? FeeSat { get; set; }

    /// <summary>Where phoenixd posts its own payment notification, when the merchant asked for one.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>The <c>Idempotency-Key</c> the invoice was created under, unique per merchant.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// SHA-256 of the creation request. A key replayed with a different request is refused rather than
    /// answered with an invoice for something else.
    /// </summary>
    public string? RequestHash { get; set; }
}
