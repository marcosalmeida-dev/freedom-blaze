namespace FreedomBlaze.Models;

/// <summary>Read model for a persisted <see cref="Data.Entities.Donation"/>.</summary>
public sealed record DonationDto
{
    public int Id { get; init; }

    public string? PaymentHash { get; init; }

    public long AmountSat { get; init; }

    public long ReceivedSat { get; init; }

    public string? Message { get; init; }

    public string Description { get; init; } = string.Empty;

    public string? FiatEstimate { get; init; }

    /// <summary>The donation outcome as its <see cref="Data.Entities.DonationStatus"/> name.</summary>
    public string Status { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }
}
