using FreedomBlaze.Models;

namespace FreedomBlaze.Services;

/// <summary>
/// Persists Lightning donations and announces each outcome on Telegram. Implementations are
/// best-effort: a storage or notification fault is logged and swallowed so it never disrupts the
/// donor's experience in the dialog.
/// </summary>
public interface IDonationService
{
    /// <summary>Persists a settled donation and sends a styled success notification to Telegram.</summary>
    Task<DonationDto> RecordSuccessAsync(DonationDetails details, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a failed donation attempt and sends a formatted error report to Telegram.
    /// </summary>
    Task<DonationDto> RecordFailureAsync(DonationDetails details, string error, CancellationToken cancellationToken = default);
}
