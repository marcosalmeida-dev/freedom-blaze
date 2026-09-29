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
    /// persists them via OpenAI on the first request for any recent day not yet stored.
    /// </summary>
    /// <remarks>
    /// A day with no news returns an empty array; a day whose news could not be produced returns
    /// <c>503</c> with a problem detail explaining why, so callers can tell the two apart.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NewsArticleModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Get([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var result = await newsService.GetNewsAsync(date ?? newsService.Today, cancellationToken: cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Forces a fresh OpenAI web-search generation for the given date (defaults to today),
    /// overwriting any existing articles. Triggers a paid OpenAI API call.
    /// </summary>
    [HttpPost("refresh")]
    [Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<NewsArticleModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<NewsArticleModel>>> Refresh([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var result = await newsService.RefreshNewsAsync(date ?? newsService.Today, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Returns all dates for which a saved news set exists (used by the date-picker UI).</summary>
    [HttpGet("dates")]
    [ProducesResponseType(typeof(IReadOnlyList<DateOnly>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DateOnly>>> GetAvailableDates(CancellationToken cancellationToken)
    {
        var dates = await newsService.GetAvailableDatesAsync(cancellationToken);
        return Ok(dates);
    }

    /// <summary>
    /// Maps a lookup outcome onto the wire: articles on success, and a problem detail on failure so
    /// the caller sees a real reason rather than an empty list that looks like a quiet day.
    /// </summary>
    private ActionResult<IReadOnlyList<NewsArticleModel>> ToActionResult(NewsResult result) =>
        result.Status == NewsStatus.Unavailable
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Bitcoin news unavailable",
                Detail = result.Message,
            })
            : Ok(result.Articles);
}
