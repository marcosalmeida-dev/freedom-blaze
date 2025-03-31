using Microsoft.AspNetCore.Mvc;

[Route("api/bitcoin-news")]
[ApiController]
public class BitcoinNewsController : ControllerBase
{
    private readonly ChatGptService _chatGptService;

    public BitcoinNewsController(ChatGptService chatGptService)
    {
        _chatGptService = chatGptService;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var news = await _chatGptService.GetTodayBitcoinNewsAsync();
        return Ok(news);
    }

    [HttpGet]
    [Route("get-chatgpt-news")]
    public async Task<IActionResult> GetBitcoinChatGptNews()
    {
        try 
        {
            await _chatGptService.GetBitcoinChatGptNews();
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
