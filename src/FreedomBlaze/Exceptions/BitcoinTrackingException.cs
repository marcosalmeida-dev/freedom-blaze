namespace FreedomBlaze.Exceptions;

public enum BitcoinTrackingError { InvalidInput, NotFound, RateLimited, Unavailable }

/// <summary>A stable error code for localized UI messages. Provider bodies and searched identifiers are excluded.</summary>
public sealed class BitcoinTrackingException(BitcoinTrackingError error) : Exception(error.ToString())
{
    public BitcoinTrackingError Error { get; } = error;
}
