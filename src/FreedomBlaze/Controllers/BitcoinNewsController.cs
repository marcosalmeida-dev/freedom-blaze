using FreedomBlaze.Client.Models;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

[Route("api/bitcoin-news")]
[ApiController]
public class BitcoinNewsController(BitcoinNewsService newsService) : ControllerBase
{
    /// <summary>
    /// Returns the Bitcoin news for <paramref name="date"/> (defaults to today), generating and
    /// persisting it on the first request for a day that isn't already stored.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Get([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var news = await newsService.GetNewsAsync(date ?? newsService.Today, cancellationToken);
        return Ok(news);
    }

    /// <summary>Forces a fresh web-search generation for <paramref name="date"/> (defaults to today). Triggers a paid OpenAI call.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Refresh([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var news = await newsService.RefreshNewsAsync(date ?? newsService.Today, cancellationToken);
        return Ok(news);
    }

    /// <summary>Returns the dates that already have a saved news set (drives the date filter).</summary>
    [HttpGet("dates")]
    public async Task<ActionResult<IReadOnlyList<DateOnly>>> GetAvailableDates(CancellationToken cancellationToken)
    {
        var dates = await newsService.GetAvailableDatesAsync(cancellationToken);
        return Ok(dates);
    }
}
