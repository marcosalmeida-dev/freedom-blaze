using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

[ApiController]
[Route("api/contact")]
public class ContactController : Controller
{
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public ContactController(IConfiguration config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    [HttpPost("submit")]
    public async Task<IActionResult> Submit(ContactFormModel model)
    {
        if (ModelState.IsValid)
        {
            var telegramBotToken = _config["TelegramBotFBToken"];
            var chatId = _config["TelegramFBContactChannelId"];
            var message = $"Title: {model.Title}\nDescription: {model.Description}";

            var url = $"https://api.telegram.org/bot{telegramBotToken}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                return Ok(new { message = "Message sent to Telegram successfully." });
            }
            else
            {
                return StatusCode(500, "Error sending message to Telegram.");
            }
        }

        return BadRequest("Invalid data");
    }
}

public class ContactFormModel
{
    [Required]
    [StringLength(128, MinimumLength = 3, ErrorMessage = "Title length can't be more than 128, and minimum 3.")]
    public string Title { get; set; }

    [Required]
    [StringLength(2048, MinimumLength = 3, ErrorMessage = "Title length can't be more than 2048, and minimum 3.")]
    public string Description { get; set; }
}
