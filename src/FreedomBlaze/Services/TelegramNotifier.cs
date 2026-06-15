using FreedomBlaze.Options;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// <see cref="ITelegramNotifier"/> backed by the Telegram Bot API. Uses the named "Telegram"
/// <see cref="IHttpClientFactory"/> client (pooled handler + standard resilience) and posts with
/// <c>parse_mode=HTML</c> so callers can send richly formatted messages.
/// </summary>
public class TelegramNotifier(
    IHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramNotifier> logger) : ITelegramNotifier
{
    private readonly TelegramOptions _options = options.Value;

    public async Task<bool> SendHtmlAsync(string? chatId, string html, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.TelegramBotFBToken) || string.IsNullOrWhiteSpace(chatId))
        {
            logger.LogWarning("Telegram notification skipped: bot token or chat id is not configured.");
            return false;
        }

        try
        {
            var httpClient = httpClientFactory.CreateClient("Telegram");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = html,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = "true",
            });

            using var response = await httpClient.PostAsync(
                $"/bot{_options.TelegramBotFBToken}/sendMessage", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Telegram sendMessage failed ({StatusCode}): {Body}", response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram notification.");
            return false;
        }
    }
}
