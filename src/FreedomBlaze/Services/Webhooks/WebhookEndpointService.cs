using System.Security.Cryptography;
using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services.Webhooks;

/// <summary>Filters for a merchant's delivery log; <see cref="Before"/> is the paging cursor.</summary>
public sealed record WebhookDeliveryQuery(WebhookDeliveryState? State = null, int? EndpointId = null, int? Limit = null, long? Before = null);

/// <summary>Manages merchants' webhook endpoints and their delivery log. Everything is scoped to one merchant.</summary>
public interface IWebhookEndpointService
{
    /// <exception cref="RequestValidationException">The URL is unacceptable, or no credential key is configured.</exception>
    Task<WebhookEndpointWithSecret> CreateAsync(int merchantId, CreateWebhookEndpointRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookEndpointInfo>> ListAsync(int merchantId, CancellationToken cancellationToken);

    /// <summary>Null when the merchant has no such endpoint. Disabling fails its pending deliveries.</summary>
    /// <exception cref="RequestValidationException">The new URL is unacceptable.</exception>
    Task<WebhookEndpointInfo?> UpdateAsync(int merchantId, int endpointId, UpdateWebhookEndpointRequest request, CancellationToken cancellationToken);

    /// <summary>A new signing secret, used from the next attempt on. Null when there is no such endpoint.</summary>
    Task<WebhookEndpointWithSecret?> RotateSecretAsync(int merchantId, int endpointId, CancellationToken cancellationToken);

    /// <summary>Posts a <c>webhook.test</c> event now and reports what happened. Null when there is no such endpoint.</summary>
    Task<WebhookTestResult?> SendTestAsync(int merchantId, int endpointId, CancellationToken cancellationToken);

    Task<WebhookDeliveryPage> ListDeliveriesAsync(int merchantId, WebhookDeliveryQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Queues the delivery's event again, as a new delivery with the same event id. Null when the merchant has no
    /// such delivery.
    /// </summary>
    /// <exception cref="RequestValidationException">Its endpoint is disabled.</exception>
    Task<WebhookDeliveryInfo?> RedeliverAsync(int merchantId, long deliveryId, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IWebhookEndpointService"/>.</summary>
public sealed class WebhookEndpointService(
    IDbContextFactory<FreedomBlazeDbContext> contexts,
    ILightningCredentialProtector credentials,
    WebhookDispatcher dispatcher,
    IOptions<LightningWebhookOptions> webhookOptions,
    IOptions<LightningInvoiceOptions> invoiceOptions,
    TimeProvider timeProvider,
    ILogger<WebhookEndpointService> logger) : IWebhookEndpointService
{
    public const int MaxEndpointsPerMerchant = 10;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<WebhookEndpointWithSecret> CreateAsync(int merchantId, CreateWebhookEndpointRequest request, CancellationToken cancellationToken)
    {
        var url = ParseUrl(request.Url);
        if (!credentials.CanProtect)
            throw new RequestValidationException(
                "This server cannot store signing secrets yet: configure LightningBackends:CredentialKeys first.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.WebhookEndpoints.CountAsync(e => e.MerchantId == merchantId && e.IsActive, cancellationToken) >= MaxEndpointsPerMerchant)
            throw new RequestValidationException($"A merchant can have at most {MaxEndpointsPerMerchant} active endpoints.");

        var secret = WebhookSignature.NewSecret();
        var endpoint = new WebhookEndpoint
        {
            MerchantId = merchantId,
            Url = url,
            Description = NullIfBlank(request.Description),
            ProtectedSecret = credentials.Protect(merchantId, secret, CredentialPurpose.WebhookSecret),
            IsActive = true,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Webhook endpoint {EndpointId} of merchant {MerchantId} registered for {Url}.", endpoint.Id, merchantId, url);
        return WebhookEndpointWithSecret.From(endpoint, secret);
    }

    public async Task<IReadOnlyList<WebhookEndpointInfo>> ListAsync(int merchantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var endpoints = await db.WebhookEndpoints.AsNoTracking()
            .Where(e => e.MerchantId == merchantId)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);
        return endpoints.Select(WebhookEndpointInfo.From).ToList();
    }

    public async Task<WebhookEndpointInfo?> UpdateAsync(int merchantId, int endpointId, UpdateWebhookEndpointRequest request, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == endpointId && e.MerchantId == merchantId, cancellationToken);
        if (endpoint is null)
            return null;

        if (request.Url is not null)
            endpoint.Url = ParseUrl(request.Url);
        if (request.Description is not null)
            endpoint.Description = NullIfBlank(request.Description);

        var now = timeProvider.GetUtcNow();
        if (request.IsActive is { } isActive && isActive != endpoint.IsActive)
        {
            endpoint.IsActive = isActive;
            endpoint.DisabledAtUtc = isActive ? null : now;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (!endpoint.IsActive)
        {
            var failed = await db.WebhookDeliveries
                .Where(d => d.EndpointId == endpoint.Id && d.State == WebhookDeliveryState.Pending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(d => d.State, WebhookDeliveryState.Failed)
                    .SetProperty(d => d.LastError, "The endpoint was disabled.")
                    .SetProperty(d => d.NextAttemptAtUtc, (DateTimeOffset?)null)
                    .SetProperty(d => d.ClaimedUntilUtc, (DateTimeOffset?)null), cancellationToken);
            logger.LogInformation("Webhook endpoint {EndpointId} of merchant {MerchantId} disabled; {Failed} pending deliveries failed.",
                endpoint.Id, merchantId, failed);
        }

        return WebhookEndpointInfo.From(endpoint);
    }

    public async Task<WebhookEndpointWithSecret?> RotateSecretAsync(int merchantId, int endpointId, CancellationToken cancellationToken)
    {
        if (!credentials.CanProtect)
            throw new RequestValidationException(
                "This server cannot store signing secrets yet: configure LightningBackends:CredentialKeys first.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == endpointId && e.MerchantId == merchantId, cancellationToken);
        if (endpoint is null)
            return null;

        var secret = WebhookSignature.NewSecret();
        endpoint.ProtectedSecret = credentials.Protect(merchantId, secret, CredentialPurpose.WebhookSecret);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Rotated the signing secret of webhook endpoint {EndpointId} of merchant {MerchantId}.", endpoint.Id, merchantId);
        return WebhookEndpointWithSecret.From(endpoint, secret);
    }

    public async Task<WebhookTestResult?> SendTestAsync(int merchantId, int endpointId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var endpoint = await db.WebhookEndpoints.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == endpointId && e.MerchantId == merchantId, cancellationToken);
        if (endpoint is null)
            return null;

        string secret;
        try
        {
            secret = credentials.Unprotect(merchantId, endpoint.ProtectedSecret, CredentialPurpose.WebhookSecret);
        }
        catch (CryptographicException)
        {
            return new WebhookTestResult { Delivered = false, Error = "The endpoint's signing secret cannot be decrypted on this server." };
        }

        var eventId = WebhookEvent.NewId();
        var payload = WebhookEvent.Serialize(eventId, WebhookEventTypes.Test, timeProvider.GetUtcNow(),
            new { merchantId, endpointId, message = "A test event from the FreedomBlaze Lightning gateway." });
        var attempt = await dispatcher.SendAsync(endpoint.Url, secret, eventId, payload, cancellationToken);

        return new WebhookTestResult
        {
            Delivered = attempt.Delivered,
            StatusCode = attempt.StatusCode,
            DurationMs = (int)attempt.Duration.TotalMilliseconds,
            Error = attempt.Error,
        };
    }

    public async Task<WebhookDeliveryPage> ListDeliveriesAsync(int merchantId, WebhookDeliveryQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultPageSize, 1, MaxPageSize);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var matching = db.WebhookDeliveries.AsNoTracking().Where(d => d.MerchantId == merchantId);
        if (query.State is { } state)
            matching = matching.Where(d => d.State == state);
        if (query.EndpointId is { } endpointId)
            matching = matching.Where(d => d.EndpointId == endpointId);
        if (query.Before is { } before)
            matching = matching.Where(d => d.Id < before);

        // One extra row says whether another page exists without a separate count.
        var page = await matching.OrderByDescending(d => d.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var items = page.Take(limit).Select(WebhookDeliveryInfo.From).ToList();
        return new WebhookDeliveryPage { Items = items, NextCursor = page.Count > limit ? items[^1].Id : null };
    }

    public async Task<WebhookDeliveryInfo?> RedeliverAsync(int merchantId, long deliveryId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var original = await db.WebhookDeliveries.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.MerchantId == merchantId, cancellationToken);
        if (original is null)
            return null;

        if (!await db.WebhookEndpoints.AnyAsync(e => e.Id == original.EndpointId && e.IsActive, cancellationToken))
            throw new RequestValidationException("The delivery's endpoint is disabled; enable it before redelivering.");

        var now = timeProvider.GetUtcNow();
        var copy = new WebhookDelivery
        {
            MerchantId = merchantId,
            EndpointId = original.EndpointId,
            EventId = original.EventId,
            EventType = original.EventType,
            InvoiceId = original.InvoiceId,
            Payload = original.Payload,
            State = WebhookDeliveryState.Pending,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
        };
        db.WebhookDeliveries.Add(copy);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Redelivering {EventType} {EventId} to endpoint {EndpointId} as delivery {DeliveryId}.",
            copy.EventType, copy.EventId, copy.EndpointId, copy.Id);
        return WebhookDeliveryInfo.From(copy);
    }

    private string ParseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            throw new RequestValidationException("url must be an absolute HTTPS URL.");

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && webhookOptions.Value.AllowInsecureHttp))
            throw new RequestValidationException("url must use HTTPS.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new RequestValidationException("url must not embed credentials.");

        var allowed = invoiceOptions.Value.AllowedWebhookHosts;
        if (allowed.Length > 0 && !allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new RequestValidationException($"url host '{uri.Host}' is not in LightningInvoices:AllowedWebhookHosts.");

        return uri.AbsoluteUri;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
