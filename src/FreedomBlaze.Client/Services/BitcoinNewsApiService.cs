using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Client.Models;

namespace FreedomBlaze.Client.Services;

/// <summary>
/// WebAssembly-side <see cref="IBitcoinNewsApiService"/> implementation: reads Bitcoin news from the
/// server API over HTTP (against the browser origin).
/// <para>
/// Transport problems are turned into a <see cref="NewsStatus.Unavailable"/> result rather than an
/// exception, so the page can show the reason the server gave instead of a generic failure.
/// </para>
/// </summary>
public class BitcoinNewsApiService(HttpClient httpClient, TimeProvider? timeProvider = null) : IBitcoinNewsApiService
{
    private const string BasePath = "api/bitcoin-news";

    private const string GenericFailure =
        "Bitcoin news is temporarily unavailable. Please try again in a moment.";

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="allowGeneration"/> is retained for compatibility and ignored. The GET
    /// endpoint only reads editions produced by the server's background worker.
    /// </remarks>
    public Task<NewsResult> GetNewsAsync(DateOnly date, bool allowGeneration = false, CancellationToken cancellationToken = default)
        => ReadNewsAsync(date, cancellationToken);

    public async Task<NewsResult> RetryNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(12));
        var retryToken = timeout.Token;
        try
        {
            using var response = await httpClient.PostAsync($"{BasePath}/retry?date={date:yyyy-MM-dd}", null, retryToken);
            if (!response.IsSuccessStatusCode)
                return NewsResult.Unavailable(await ReadFailureMessageAsync(response, retryToken));
            var operation = await response.Content.ReadFromJsonAsync<NewsRetryOperation>(retryToken);
            while (operation is { IsCompleted: false })
            {
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider ?? TimeProvider.System, retryToken);
                using var status = await httpClient.GetAsync($"{BasePath}/retry/{operation.Id}", retryToken);
                if (!status.IsSuccessStatusCode)
                    return NewsResult.Unavailable(await ReadFailureMessageAsync(status, retryToken));
                operation = await status.Content.ReadFromJsonAsync<NewsRetryOperation>(retryToken);
            }
            return operation?.Result ?? NewsResult.Unavailable(GenericFailure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            return NewsResult.Unavailable(GenericFailure);
        }
    }

    private async Task<NewsResult> ReadNewsAsync(DateOnly date, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"{BasePath}?date={date:yyyy-MM-dd}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return NewsResult.Unavailable(await ReadFailureMessageAsync(response, cancellationToken));
            }

            var articles = await response.Content.ReadFromJsonAsync<List<NewsArticleModel>>(cancellationToken);
            return NewsResult.Ok(articles ?? []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Caller navigated away or started a newer request; not a failure to report.
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            // Offline, timed out, or an unreadable payload — all the same to the reader.
            return NewsResult.Unavailable(GenericFailure);
        }
    }

    public async Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dates = await httpClient.GetFromJsonAsync<List<DateOnly>>($"{BasePath}/dates", cancellationToken);
            return dates ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            // Non-fatal: the date filter simply falls back to today only.
            return [];
        }
    }

    /// <summary>
    /// Pulls the reader-facing explanation out of the RFC 9457 problem response the API returns for
    /// an unavailable day, falling back to a generic message for anything else (including 429s from
    /// the rate limiter, which have no body).
    /// </summary>
    private static async Task<string> ReadFailureMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "You're loading news a bit too quickly. Please wait a moment and try again.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(cancellationToken);
            return string.IsNullOrWhiteSpace(problem?.Detail) ? GenericFailure : problem.Detail;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or NotSupportedException)
        {
            return GenericFailure;
        }
    }

    /// <summary>The subset of the problem response the UI actually needs.</summary>
    private sealed record ProblemPayload([property: JsonPropertyName("detail")] string? Detail);
}
