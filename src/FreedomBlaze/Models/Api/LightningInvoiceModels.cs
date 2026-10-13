using System.ComponentModel.DataAnnotations;
using FreedomBlaze.Options;

namespace FreedomBlaze.Models.Api;

/// <summary>
/// Request to issue a BOLT11 invoice priced in a fiat currency (or directly in <c>BTC</c>/<c>SATS</c>).
/// The amount is converted at the current rate and the invoice is created on the node in one step,
/// so the quote and the invoice cannot drift apart.
/// </summary>
public sealed class CreateLightningInvoiceRequest
{
    /// <summary>Amount to charge, in <see cref="Currency"/>. Must be greater than zero.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 fiat code (e.g. <c>BRL</c>) or the Bitcoin units <c>BTC</c>/<c>SATS</c>.</summary>
    [Required(ErrorMessage = "The currency code is required.")]
    [StringLength(8, MinimumLength = 2)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// The caller's own reference (e.g. an order id), returned on status reads. Letters, digits and
    /// <c>. _ : -</c> only.
    /// </summary>
    [StringLength(100)]
    [RegularExpression(@"^[A-Za-z0-9._:\-]+$", ErrorMessage = "externalId may only contain letters, digits and . _ : -")]
    public string? ExternalId { get; set; }

    /// <summary>Text the payer's wallet shows. At most 128 characters (a phoenixd limit).</summary>
    [StringLength(128)]
    public string? Description { get; set; }

    /// <summary>Invoice lifetime in seconds, which is also how long the quoted price holds. Defaults to 600.</summary>
    [Range(LightningInvoiceOptions.MinExpirySeconds, LightningInvoiceOptions.MaxExpirySeconds)]
    public int? ExpirySeconds { get; set; }

    /// <summary>
    /// Optional HTTPS endpoint phoenixd calls once when the invoice is paid, signed with the node's
    /// webhook secret in <c>X-Phoenix-Signature</c>. Delivery is attempted once and never retried, so
    /// callers must still poll the status endpoint.
    /// </summary>
    [StringLength(2048)]
    public string? WebhookUrl { get; set; }
}

/// <summary>An issued invoice together with the quote that priced it.</summary>
public sealed class LightningInvoiceResponse
{
    /// <summary>Identifies the invoice on the status endpoint.</summary>
    public string PaymentHash { get; init; } = string.Empty;

    /// <summary>The BOLT11 payment request to show as a QR code.</summary>
    public string Bolt11 { get; init; } = string.Empty;

    /// <summary>Satoshis the invoice asks for (the fiat amount, rounded up).</summary>
    public long AmountSat { get; init; }

    /// <summary>The amount that was priced, in <see cref="Currency"/>.</summary>
    public decimal Amount { get; init; }

    /// <summary>Normalised currency or Bitcoin unit code.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Price of one bitcoin in <see cref="Currency"/> that the quote used.</summary>
    public decimal BitcoinPrice { get; init; }

    /// <summary>The aggregated BTC/USD price, or <c>null</c> for invoices priced in BTC/SATS.</summary>
    public decimal? BitcoinPriceUsd { get; init; }

    /// <summary>How many exchanges the BTC/USD price came from.</summary>
    public int RateSources { get; init; }

    /// <summary>When the fiat rate was published; <c>null</c> when no fiat conversion was involved.</summary>
    public DateTimeOffset? FiatRateTimestamp { get; init; }

    /// <summary>When the price was quoted (UTC).</summary>
    public DateTimeOffset QuotedAt { get; init; }

    /// <summary>When the invoice stops being payable (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The caller's reference, as supplied.</summary>
    public string? ExternalId { get; init; }
}

/// <summary>Live state of an invoice, read from the node.</summary>
public sealed class LightningInvoiceStatusResponse
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string Expired = "expired";

    public string PaymentHash { get; init; } = string.Empty;

    /// <summary>The caller's reference supplied at creation.</summary>
    public string? ExternalId { get; init; }

    /// <summary><c>pending</c>, <c>paid</c> or <c>expired</c>.</summary>
    public string Status { get; init; } = Pending;

    public bool IsPaid { get; init; }

    /// <summary>Satoshis the invoice asked for.</summary>
    public long? RequestedSat { get; init; }

    /// <summary>Satoshis the node received.</summary>
    public long ReceivedSat { get; init; }

    /// <summary>Fees the node paid on receipt (e.g. buying inbound liquidity), in satoshis, rounded up.</summary>
    public long FeeSat { get; init; }

    /// <summary>The BOLT11 payment request, for re-displaying the QR code.</summary>
    public string? Bolt11 { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Whether the gateway can currently issue invoices.</summary>
public sealed class LightningGatewayStatus
{
    /// <summary>A phoenixd node is configured on this server.</summary>
    public bool Enabled { get; init; }

    /// <summary>The node answered just now.</summary>
    public bool Reachable { get; init; }
}
