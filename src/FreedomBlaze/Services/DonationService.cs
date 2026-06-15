using System.Globalization;
using System.Net;
using System.Text;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Mappings;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Persists Lightning donations to the database and announces each outcome on Telegram — a styled
/// celebration on success, a clearly formatted error report on failure. Every operation is
/// best-effort: storage and notification faults are logged and swallowed so a backend hiccup never
/// breaks the donor's experience in the donate dialog.
/// </summary>
public class DonationService(
    IRepository<Donation> donations,
    ITelegramNotifier telegram,
    IOptions<TelegramOptions> telegramOptions,
    TimeProvider timeProvider,
    ILogger<DonationService> logger) : IDonationService
{
    private const int MaxErrorLength = 2000;
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    public async Task<DonationDto> RecordSuccessAsync(DonationDetails details, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var donation = new Donation
        {
            PaymentHash = details.PaymentHash,
            AmountSat = details.AmountSat,
            ReceivedSat = details.ReceivedSat,
            Message = Normalize(details.Message),
            Description = details.Description,
            Bolt11 = details.Bolt11,
            FiatEstimate = details.FiatEstimate,
            Status = DonationStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };

        await SaveAsync(donation, cancellationToken);
        await NotifyAsync(BuildSuccessMessage(donation), cancellationToken);

        return donation.ToDto();
    }

    public async Task<DonationDto> RecordFailureAsync(DonationDetails details, string error, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var donation = new Donation
        {
            PaymentHash = details.PaymentHash,
            AmountSat = details.AmountSat,
            ReceivedSat = details.ReceivedSat,
            Message = Normalize(details.Message),
            Description = details.Description,
            Bolt11 = details.Bolt11,
            FiatEstimate = details.FiatEstimate,
            Status = DonationStatus.Failed,
            ErrorMessage = Truncate(error, MaxErrorLength),
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };

        await SaveAsync(donation, cancellationToken);
        await NotifyAsync(BuildFailureMessage(donation), cancellationToken);

        return donation.ToDto();
    }

    private async Task SaveAsync(Donation donation, CancellationToken cancellationToken)
    {
        try
        {
            await donations.AddAsync(donation, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist donation (status {Status}, hash {PaymentHash}).",
                donation.Status, donation.PaymentHash);
        }
    }

    private async Task NotifyAsync(string html, CancellationToken cancellationToken)
    {
        // Donations get their own channel when configured, otherwise fall back to the contact channel.
        var chatId = string.IsNullOrWhiteSpace(_telegramOptions.TelegramFBDonationChannelId)
            ? _telegramOptions.TelegramFBContactChannelId
            : _telegramOptions.TelegramFBDonationChannelId;

        await telegram.SendHtmlAsync(chatId, html, cancellationToken);
    }

    private static string BuildSuccessMessage(Donation donation)
    {
        var amount = donation.ReceivedSat > 0 ? donation.ReceivedSat : donation.AmountSat;

        var sb = new StringBuilder();
        sb.Append("⚡️ <b>New Donation Received!</b> ⚡️\n\n");
        sb.Append("A fresh tip just lit up the Freedom Blaze node. 🔥\n\n");
        sb.Append($"💰 <b>Amount:</b> {FormatSats(amount)} sats\n");

        if (!string.IsNullOrWhiteSpace(donation.FiatEstimate))
        {
            sb.Append($"💵 <b>Value:</b> ≈ {Encode(donation.FiatEstimate)}\n");
        }

        if (!string.IsNullOrWhiteSpace(donation.Message))
        {
            sb.Append($"💬 <b>Message:</b> “<i>{Encode(donation.Message)}</i>”\n");
        }

        if (!string.IsNullOrWhiteSpace(donation.PaymentHash))
        {
            sb.Append($"🧾 <b>Payment hash:</b> <code>{Encode(donation.PaymentHash)}</code>\n");
        }

        sb.Append($"🕒 <b>Received:</b> {FormatTimestamp(donation.CompletedAtUtc ?? donation.CreatedAtUtc)}\n\n");
        sb.Append("🙏 <b>Thank you for fueling freedom!</b>");

        return sb.ToString();
    }

    private static string BuildFailureMessage(Donation donation)
    {
        var sb = new StringBuilder();
        sb.Append("🚨 <b>Donation Failed</b> 🚨\n\n");
        sb.Append("An incoming tip could not be completed. ⚠️\n\n");
        sb.Append($"💰 <b>Requested:</b> {FormatSats(donation.AmountSat)} sats\n");

        if (!string.IsNullOrWhiteSpace(donation.FiatEstimate))
        {
            sb.Append($"💵 <b>Value:</b> ≈ {Encode(donation.FiatEstimate)}\n");
        }

        if (!string.IsNullOrWhiteSpace(donation.Message))
        {
            sb.Append($"💬 <b>Message:</b> “<i>{Encode(donation.Message)}</i>”\n");
        }

        if (!string.IsNullOrWhiteSpace(donation.PaymentHash))
        {
            sb.Append($"🧾 <b>Payment hash:</b> <code>{Encode(donation.PaymentHash)}</code>\n");
        }

        sb.Append($"🕒 <b>When:</b> {FormatTimestamp(donation.CompletedAtUtc ?? donation.CreatedAtUtc)}\n\n");
        sb.Append("❗️ <b>Error details:</b>\n");
        sb.Append($"<pre>{Encode(donation.ErrorMessage ?? "Unknown error.")}</pre>");

        return sb.ToString();
    }

    private static string FormatSats(long sats) => sats.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>HTML-encodes dynamic text so it is safe inside a Telegram <c>parse_mode=HTML</c> message.</summary>
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
}
