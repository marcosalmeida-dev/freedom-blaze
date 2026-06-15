namespace FreedomBlaze.Models;

/// <summary>
/// Details of a Lightning donation captured by the donate dialog and handed to
/// <see cref="Services.IDonationService"/> to be persisted and announced. The same shape describes a
/// settled tip and a failed attempt; the service stamps the final outcome.
/// </summary>
public sealed record DonationDetails
{
    /// <summary>Amount requested on the invoice, in satoshis.</summary>
    public long AmountSat { get; init; }

    /// <summary>Amount actually received, in satoshis (0 for a failed attempt).</summary>
    public long ReceivedSat { get; init; }

    /// <summary>Optional message left by the donor.</summary>
    public string? Message { get; init; }

    /// <summary>The invoice description shown to the payer.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The BOLT11 payment hash, when an invoice was generated.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>The serialized BOLT11 payment request, when one was generated.</summary>
    public string? Bolt11 { get; init; }

    /// <summary>Formatted fiat estimate of the amount (e.g. "$12.34"), when available.</summary>
    public string? FiatEstimate { get; init; }
}
