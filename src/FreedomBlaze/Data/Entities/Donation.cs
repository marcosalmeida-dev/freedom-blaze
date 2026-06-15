namespace FreedomBlaze.Data.Entities;

/// <summary>
/// A single Lightning donation to Freedom Blaze. A row is written when an invoice is settled
/// (<see cref="DonationStatus.Completed"/>) or when an attempt errors before payment
/// (<see cref="DonationStatus.Failed"/>), giving a complete, auditable history of tips.
/// </summary>
public class Donation
{
    public int Id { get; set; }

    /// <summary>
    /// The BOLT11 payment hash that uniquely identifies the invoice, or <c>null</c> for an attempt
    /// that failed before an invoice could be created.
    /// </summary>
    public string? PaymentHash { get; set; }

    /// <summary>Amount requested on the invoice, in satoshis.</summary>
    public long AmountSat { get; set; }

    /// <summary>Amount actually received, in satoshis (0 until/unless the invoice is paid).</summary>
    public long ReceivedSat { get; set; }

    /// <summary>Optional message left by the donor.</summary>
    public string? Message { get; set; }

    /// <summary>The invoice description shown to the payer.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The serialized BOLT11 payment request, when one was generated.</summary>
    public string? Bolt11 { get; set; }

    /// <summary>Formatted fiat estimate of the amount at donation time (e.g. "$12.34"), when available.</summary>
    public string? FiatEstimate { get; set; }

    /// <summary>Lifecycle outcome of the donation.</summary>
    public DonationStatus Status { get; set; }

    /// <summary>Error detail captured when <see cref="Status"/> is <see cref="DonationStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>When the row was created (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the donation reached its terminal state — paid or failed (UTC).</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
