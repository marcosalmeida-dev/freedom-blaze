using FreedomBlaze.Authentication;
using FreedomBlaze.Client.Models;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FreedomBlaze.Controllers;

[Route("api/bitcoin-news")]
[ApiController]
[Tags("Bitcoin News")]
[Produces("application/json")]
[EnableRateLimiting(RateLimitPolicies.PerIp)]
public sealed class BitcoinNewsController(BitcoinNewsService newsService) : ControllerBase
{
    /// <summary>
    /// Returns the Bitcoin news articles for the given date (defaults to today). Generates and
    /// persists them via OpenAI on the first request for any day not yet stored.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NewsArticleModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Get([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var news = await newsService.GetNewsAsync(date ?? newsService.Today, cancellationToken);
        return Ok(news);
    }

    /// <summary>
    /// Forces a fresh OpenAI web-search generation for the given date (defaults to today),
    /// overwriting any existing articles. Triggers a paid OpenAI API call.
    /// </summary>
    [HttpPost("refresh")]
    [Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<NewsArticleModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Refresh([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var news = await newsService.RefreshNewsAsync(date ?? newsService.Today, cancellationToken);
        return Ok(news);
    }

    /// <summary>Returns all dates for which a saved news set exists (used by the date-picker UI).</summary>
    [HttpGet("dates")]
    [ProducesResponseType(typeof(IReadOnlyList<DateOnly>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DateOnly>>> GetAvailableDates(CancellationToken cancellationToken)
    {
        var dates = await newsService.GetAvailableDatesAsync(cancellationToken);
        return Ok(dates);
    }
}
