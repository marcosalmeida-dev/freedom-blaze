using FreedomBlaze.Client.Models;
using FreedomBlaze.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Controllers;

[ApiController]
[Route("api/contact")]
public class ContactController(IHttpClientFactory httpClientFactory, IOptions<TelegramOptions> telegramOptions) : ControllerBase
{
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    [HttpPost("submit")]
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
