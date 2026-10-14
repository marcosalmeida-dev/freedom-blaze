namespace FreedomBlaze.Exceptions;

/// <summary>
/// A management request that is well-formed but cannot be honoured (an unknown merchant, a scope that
/// needs a merchant, …). Controllers answer it with HTTP 400 and the message as the problem detail.
/// </summary>
public class RequestValidationException(string message) : Exception(message);
