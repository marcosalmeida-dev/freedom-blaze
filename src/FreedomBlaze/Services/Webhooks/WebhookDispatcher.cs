using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services.Webhooks;

/// <summary>One attempt to post an event.</summary>
public sealed record WebhookAttempt(bool Delivered, int? StatusCode, string? Error, TimeSpan Duration);

/// <summary>
/// Sends due webhook deliveries. Each is claimed before it is sent, so two instances never send the same one,
/// and a failed attempt is retried on <see cref="RetrySchedule"/> (about a day in all) before it is marked failed.
/// Receivers must answer 2xx quickly and treat <c>webhook-id</c> as an idempotency key: a delivery whose answer
/// was lost is sent again.
/// </summary>
public sealed class WebhookDispatcher(
    IDbContextFactory<FreedomBlazeDbContext> contexts,
    IHttpClientFactory httpClients,
    ILightningCredentialProtector credentials,
    IOptions<LightningWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookDispatcher> logger)
{
    public const string HttpClientName = "LightningWebhooks";

    /// <summary>The wait before each retry; after the last one the delivery is failed (8 attempts in all).</summary>
    public static readonly IReadOnlyList<TimeSpan> RetrySchedule =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
        TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(10),
    ];

    public static int MaxAttempts => RetrySchedule.Count + 1;

    /// <summary>Longer than one attempt can take, so a claim only lapses when its instance died.</summary>
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(2);

    private const int MaxErrorLength = 512;
    private const int MaxResponseSnippet = 200;

    /// <summary>Sends the deliveries that are due. Returns how many were attempted.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var due = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.State == WebhookDeliveryState.Pending && d.NextAttemptAtUtc != null && d.NextAttemptAtUtc <= now
                && (d.ClaimedUntilUtc == null || d.ClaimedUntilUtc < now))
            .OrderBy(d => d.NextAttemptAtUtc)
            .Take(Math.Max(1, options.Value.BatchSize))
            .ToListAsync(cancellationToken);

        var attempted = 0;
        foreach (var delivery in due)
        {
            var claimedUntil = now + ClaimDuration;
            var claimed = await db.WebhookDeliveries
                .Where(d => d.Id == delivery.Id && d.State == WebhookDeliveryState.Pending
                    && (d.ClaimedUntilUtc == null || d.ClaimedUntilUtc < now))
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.ClaimedUntilUtc, claimedUntil), cancellationToken);
            if (claimed == 0)
                continue;

            await AttemptAsync(db, delivery, cancellationToken);
            attempted++;
        }

        return attempted;
    }

    /// <summary>Posts one signed event to <paramref name="url"/>. Never throws for the endpoint's failures.</summary>
    public async Task<WebhookAttempt> SendAsync(string url, string secret, string eventId, string payload, CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.IdHeader, eventId);
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, eventId, timestamp, payload));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FreedomBlaze-Webhooks", "1.0"));

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return new WebhookAttempt(true, status, null, Stopwatch.GetElapsedTime(started));

            var snippet = await ReadSnippetAsync(response, cancellationToken);
            return new WebhookAttempt(false, status, snippet.Length > 0 ? $"HTTP {status}: {snippet}" : $"HTTP {status}",
                Stopwatch.GetElapsedTime(started));
        }
        catch (HttpRequestException ex)
        {
            return new WebhookAttempt(false, null, $"Could not reach the endpoint: {ex.Message}", Stopwatch.GetElapsedTime(started));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookAttempt(false, null, "The endpoint did not answer in time.", Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task AttemptAsync(FreedomBlazeDbContext db, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, cancellationToken);
        if (endpoint is not { IsActive: true })
        {
            await FinishAsync(db, delivery, WebhookDeliveryState.Failed, attempts: delivery.Attempts, statusCode: null,
                error: "The endpoint is disabled.", cancellationToken);
            return;
        }

        WebhookAttempt attempt;
        try
        {
            var secret = credentials.Unprotect(endpoint.MerchantId, endpoint.ProtectedSecret, CredentialPurpose.WebhookSecret);
            attempt = await SendAsync(endpoint.Url, secret, delivery.EventId, delivery.Payload, cancellationToken);
        }
        catch (CryptographicException ex)
        {
            // The key may be restored; until then this counts as a failed attempt like any other.
            logger.LogError(ex, "The signing secret of webhook endpoint {EndpointId} cannot be decrypted.", endpoint.Id);
            attempt = new WebhookAttempt(false, null, "The endpoint's signing secret cannot be decrypted on this server.", TimeSpan.Zero);
        }

        var attempts = delivery.Attempts + 1;
        if (attempt.Delivered)
        {
            await FinishAsync(db, delivery, WebhookDeliveryState.Delivered, attempts, attempt.StatusCode, null, cancellationToken);
            logger.LogInformation("Delivered {EventType} {EventId} to endpoint {EndpointId} in {Duration} ms (attempt {Attempt}).",
                delivery.EventType, delivery.EventId, endpoint.Id, (int)attempt.Duration.TotalMilliseconds, attempts);
            return;
        }

        if (attempts >= MaxAttempts)
        {
            await FinishAsync(db, delivery, WebhookDeliveryState.Failed, attempts, attempt.StatusCode, attempt.Error, cancellationToken);
            logger.LogError("Gave up delivering {EventType} {EventId} to endpoint {EndpointId} after {Attempts} attempts: {Error}",
                delivery.EventType, delivery.EventId, endpoint.Id, attempts, attempt.Error);
            return;
        }

        var nextAttempt = timeProvider.GetUtcNow() + RetrySchedule[attempts - 1];
        await db.WebhookDeliveries.Where(d => d.Id == delivery.Id).ExecuteUpdateAsync(set => set
            .SetProperty(d => d.Attempts, attempts)
            .SetProperty(d => d.LastAttemptAtUtc, timeProvider.GetUtcNow())
            .SetProperty(d => d.LastStatusCode, attempt.StatusCode)
            .SetProperty(d => d.LastError, Truncate(attempt.Error))
            .SetProperty(d => d.NextAttemptAtUtc, nextAttempt)
            .SetProperty(d => d.ClaimedUntilUtc, (DateTimeOffset?)null), cancellationToken);
        logger.LogWarning("Delivery of {EventType} {EventId} to endpoint {EndpointId} failed (attempt {Attempt}): {Error}. Retrying at {NextAttempt}.",
            delivery.EventType, delivery.EventId, endpoint.Id, attempts, attempt.Error, nextAttempt);
    }

    private async Task FinishAsync(FreedomBlazeDbContext db, WebhookDelivery delivery, WebhookDeliveryState state, int attempts,
        int? statusCode, string? error, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        DateTimeOffset? deliveredAt = state == WebhookDeliveryState.Delivered ? now : null;
        DateTimeOffset? attemptedAt = attempts > delivery.Attempts ? now : delivery.LastAttemptAtUtc;
        await db.WebhookDeliveries.Where(d => d.Id == delivery.Id).ExecuteUpdateAsync(set => set
            .SetProperty(d => d.State, state)
            .SetProperty(d => d.Attempts, attempts)
            .SetProperty(d => d.LastAttemptAtUtc, attemptedAt)
            .SetProperty(d => d.LastStatusCode, statusCode)
            .SetProperty(d => d.LastError, Truncate(error))
            .SetProperty(d => d.DeliveredAtUtc, deliveredAt)
            .SetProperty(d => d.NextAttemptAtUtc, (DateTimeOffset?)null)
            .SetProperty(d => d.ClaimedUntilUtc, (DateTimeOffset?)null), cancellationToken);
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new char[MaxResponseSnippet];
            using var reader = new StreamReader(body, Encoding.UTF8);
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            return new string(buffer, 0, read).ReplaceLineEndings(" ").Trim();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? error) =>
        error is { Length: > MaxErrorLength } ? error[..(MaxErrorLength - 1)] + "…" : error;
}
