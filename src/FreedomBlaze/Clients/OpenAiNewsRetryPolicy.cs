using System.ClientModel.Primitives;
using System.Text.Json;

namespace FreedomBlaze.Clients;

/// <summary>
/// Disables automatic transport retries by default: a timed-out paid search may already have
/// executed upstream. Explicitly configured retries still exclude exhausted credits/billing limits.
/// </summary>
public sealed class OpenAiNewsRetryPolicy(int maxRetries = 0) : ClientRetryPolicy(maxRetries)
{
    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        if (!base.ShouldRetry(message, exception))
        {
            return false;
        }

        if (exception is not null || message.Response is not { Status: 429 } response)
        {
            return true;
        }

        // Use the SDK's buffer so inspecting an error never consumes its body before the client
        // creates a ClientResultException, including when a caller requests an unbuffered response.
        return !IsQuotaFailure(response.BufferContent(message.CancellationToken));
    }

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception)
    {
        if (!base.ShouldRetry(message, exception))
        {
            return false;
        }

        if (exception is not null || message.Response is not { Status: 429 } response)
        {
            return true;
        }

        var content = await response.BufferContentAsync(message.CancellationToken).ConfigureAwait(false);
        return !IsQuotaFailure(content);
    }

    private static bool IsQuotaFailure(BinaryData content)
    {
        try
        {
            using var document = JsonDocument.Parse(content.ToMemory());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && (IsQuotaIdentifier(error, "code") || IsQuotaIdentifier(error, "type"));
        }
        catch (JsonException)
        {
            // Unknown/malformed errors retain the SDK's normal retry behavior.
            return false;
        }
    }

    private static bool IsQuotaIdentifier(JsonElement error, string propertyName) =>
        error.TryGetProperty(propertyName, out var identifier)
        && identifier.ValueKind == JsonValueKind.String
        && identifier.GetString() is "insufficient_quota"
            or "credit_balance_exhausted"
            or "billing_hard_limit_reached"
            or "organization_spend_limit_exceeded"
            or "project_spend_limit_exceeded"
            or "organization_usage_limit_exceeded";
}
