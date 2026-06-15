namespace FreedomBlaze.Services;

/// <summary>Sends formatted notifications to a Telegram chat/channel via the Bot API.</summary>
public interface ITelegramNotifier
{
    /// <summary>
    /// Sends an <c>HTML</c>-formatted message to the given chat. Returns <c>true</c> on success.
    /// Never throws — a missing configuration, transport or API fault is logged and reported as
    /// <c>false</c> so callers can treat notifications as best-effort.
    /// </summary>
    Task<bool> SendHtmlAsync(string? chatId, string html, CancellationToken cancellationToken = default);
}
