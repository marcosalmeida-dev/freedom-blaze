namespace FreedomBlaze.Data.Entities;

/// <summary>Lifecycle outcome of a <see cref="Donation"/>.</summary>
public enum DonationStatus
{
    /// <summary>An invoice was generated but has not yet been settled.</summary>
    Pending = 0,

    /// <summary>The invoice was paid — a real donation was received.</summary>
    Completed = 1,

    /// <summary>The invoice expired before being paid.</summary>
    Expired = 2,

    /// <summary>The attempt errored before completing (e.g. the invoice could not be created).</summary>
    Failed = 3,
}
