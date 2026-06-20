using FreedomBlaze.Client.Models;
using FreedomBlaze.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Controllers;

/// <summary>Forwards contact-form submissions to the Telegram notification channel.</summary>
[ApiController]
[Route("api/contact")]
[Tags("Contact")]
[Produces("application/json")]
public sealed class ContactController(IHttpClientFactory httpClientFactory, IOptions<TelegramOptions> telegramOptions) : ControllerBase
{
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    /// <summary>Submits a contact message. The message is forwarded to Telegram.</summary>
    [HttpPost("submit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit(ContactFormModel model)
    {
        var message = $"Title: {model.Title}\nDescription: {model.Description}";

        var httpClient = httpClientFactory.CreateClient("Telegram");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = _telegramOptions.TelegramFBContactChannelId ?? string.Empty,
            ["text"] = message
        });

        var response = await httpClient.PostAsync($"/bot{_telegramOptions.TelegramBotFBToken}/sendMessage", content);

        if (response.IsSuccessStatusCode)
            return Ok(new { message = "Message sent to Telegram successfully." });

        return Problem("Error sending message to Telegram.", statusCode: 500);
    }
}
