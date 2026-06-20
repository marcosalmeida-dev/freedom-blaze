namespace FreedomBlaze.Exceptions;

/// <summary>
/// Thrown when a conversion request cannot be fulfilled. <see cref="IsClientError"/> distinguishes a
/// caller mistake (unknown currency code → HTTP 400) from a transient backend problem (no live rates
/// available → HTTP 503).
/// </summary>
public class ConversionException : Exception
{
    public ConversionException(string message, bool isClientError) : base(message)
    {
        IsClientError = isClientError;
    }

    /// <summary>True when the failure is the caller's fault (bad input); false for a server-side outage.</summary>
    public bool IsClientError { get; }
}
