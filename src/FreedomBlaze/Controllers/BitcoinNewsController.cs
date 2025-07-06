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
    public async Task<IActionResult> GetBitcoinChatGptNews(string model)
    {
        try
        {
            await _chatGptService.SearchBitcoinChatGptNews(model ?? "gpt-4o");

            var news = await _chatGptService.GetTodayBitcoinNewsAsync();
            return Ok(news);
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }

        //throw new NotImplementedException();
    }

    [HttpGet]
    [Route("generate-img")]
    public async Task<IActionResult> GetGeneratedChatGptImageByText()
    {
        //try
        //{
        //    string prompt = @"Bitcoin’s hot supply craters 50% in three month";
        //    var imageUrl = await _chatGptService.GenerateChatGptImageByText(prompt);
        //    return Ok(imageUrl);
        //}
        //catch (Exception ex)
        //{
        //    return StatusCode(500, ex.Message);
        //}

        throw new NotImplementedException();
    }
}
