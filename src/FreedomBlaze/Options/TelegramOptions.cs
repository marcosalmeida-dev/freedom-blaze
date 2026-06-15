namespace FreedomBlaze.Options;

public class TelegramOptions
{
    public string? TelegramBotFBToken { get; set; }
    public string? TelegramFBContactChannelId { get; set; }

    /// <summary>
    /// Chat/channel that receives donation notifications. Optional — when unset, donation alerts fall
    /// back to <see cref="TelegramFBContactChannelId"/>.
    /// </summary>
    public string? TelegramFBDonationChannelId { get; set; }
}
