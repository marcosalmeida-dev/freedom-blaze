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
public sealed class BitcoinNewsController(BitcoinNewsService newsService, BitcoinNewsRetryQueue retryQueue) : ControllerBase
{
    /// <summary>
    /// Returns saved Bitcoin news articles for the given edition date (defaults to today in GMT-3).
    /// Generation runs on startup and daily in the background; this endpoint never calls OpenAI.
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

    /// <summary>Requests a coalesced background retry for today's missing edition.</summary>
    [HttpPost("retry")]
    [ProducesResponseType(typeof(NewsRetryOperation), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<NewsRetryOperation>> Retry([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var operation = await newsService.StartRetryAsync(date ?? newsService.Today, cancellationToken);
        return Accepted(operation);
    }

    [HttpGet("retry/{id:guid}")]
    [ProducesResponseType(typeof(NewsRetryOperation), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NewsRetryOperation>> RetryStatus(Guid id, CancellationToken cancellationToken)
    {
        // Polling can reach a different replica from the one that accepted the retry. Its shared
        // generation state is authoritative when this process has no local operation record.
        var operation = retryQueue.GetStatus(id) ?? await newsService.GetRetryStatusAsync(id, cancellationToken);
        return operation is not null ? Ok(operation) : NotFound();
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
