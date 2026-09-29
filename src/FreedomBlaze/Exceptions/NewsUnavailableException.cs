namespace FreedomBlaze.Exceptions;

/// <summary>Why a Bitcoin news generation could not be completed.</summary>
public enum NewsFailureReason
{
    /// <summary>No OpenAI API key is configured on this deployment.</summary>
    NotConfigured,

    /// <summary>The OpenAI account has no remaining credit / has hit its billing cap.</summary>
    QuotaExceeded,

    /// <summary>OpenAI throttled the request; retrying later should work.</summary>
    RateLimited,

    /// <summary>The configured API key was rejected.</summary>
    Unauthorized,

    /// <summary>The configured model rejected the request (e.g. unknown model, unsupported tool).</summary>
    ModelRejected,

    /// <summary>The call exceeded the generation timeout.</summary>
    Timeout,

    /// <summary>OpenAI returned an error or an unusable payload.</summary>
    Upstream,
}

/// <summary>
/// Signals that a news set could not be generated, carrying both a machine-readable
/// <see cref="Reason"/> (logged and used to choose the cooldown scope) and a
/// <see cref="UserMessage"/> that is safe to render.
/// </summary>
/// <remarks>
/// The exception <see cref="Exception.Message"/> keeps the upstream detail for the logs;
/// <see cref="UserMessage"/> never contains upstream error text, API keys, or model identifiers.
/// </remarks>
public sealed class NewsUnavailableException(
    NewsFailureReason reason,
    string userMessage,
    string? detail = null,
    Exception? innerException = null)
    : Exception(detail ?? userMessage, innerException)
{
    public NewsFailureReason Reason { get; } = reason;

    /// <summary>Reader-facing explanation. Safe to send to the browser.</summary>
    public string UserMessage { get; } = userMessage;
}
