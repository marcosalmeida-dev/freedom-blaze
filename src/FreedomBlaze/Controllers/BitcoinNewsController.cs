using FreedomBlaze.Models;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

[Route("api/bitcoin-news")]
[ApiController]
public class BitcoinNewsController : ControllerBase
{
    private readonly BitcoinNewsService _newsService;

    public BitcoinNewsController(BitcoinNewsService newsService)
    {
        _newsService = newsService;
    }

    /// <summary>Returns today's cached Bitcoin news, generating it on the first request of the day.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Get(CancellationToken cancellationToken)
    {
        var news = await _newsService.GetTodayBitcoinNewsAsync(cancellationToken);
        return Ok(news);
    }

    /// <summary>Forces a fresh web-search generation. Triggers a paid OpenAI call.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Refresh(CancellationToken cancellationToken)
    {
        var news = await _newsService.RefreshBitcoinNewsAsync(cancellationToken);
        return Ok(news);
    }
}
