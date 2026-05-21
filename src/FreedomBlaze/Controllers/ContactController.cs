using FreedomBlaze.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

[ApiController]
[Route("api/contact")]
public class ContactController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _telegramOptions;

    public ContactController(IHttpClientFactory httpClientFactory, IOptions<TelegramOptions> telegramOptions)
    {
        _httpClientFactory = httpClientFactory;
        _telegramOptions = telegramOptions.Value;
    }

    [HttpPost("submit")]
    public async Task<IActionResult> Submit(ContactFormModel model)
    {
        var message = $"Title: {model.Title}\nDescription: {model.Description}";

        var httpClient = _httpClientFactory.CreateClient("Telegram");
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

public class ContactFormModel
{
    [Required]
    [StringLength(128, MinimumLength = 3, ErrorMessage = "Title length can't be more than 128, and minimum 3.")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2048, MinimumLength = 3, ErrorMessage = "Description length can't be more than 2048, and minimum 3.")]
    public string Description { get; set; } = string.Empty;
}
