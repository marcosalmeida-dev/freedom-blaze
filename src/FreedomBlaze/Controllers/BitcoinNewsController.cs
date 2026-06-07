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

    /// <summary>
    /// Returns the Bitcoin news for <paramref name="date"/> (defaults to today), generating and
    /// persisting it on the first request for a day that isn't already stored.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Get([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var news = await _newsService.GetNewsForDateAsync(date ?? _newsService.Today, cancellationToken);
        return Ok(news);
    }

    /// <summary>Forces a fresh web-search generation for <paramref name="date"/> (defaults to today). Triggers a paid OpenAI call.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Refresh([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var news = await _newsService.RefreshNewsForDateAsync(date ?? _newsService.Today, cancellationToken);
        return Ok(news);
    }
}
