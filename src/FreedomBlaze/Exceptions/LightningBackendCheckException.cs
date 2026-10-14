namespace FreedomBlaze.Exceptions;

/// <summary>
/// A merchant's node failed the check that must pass before it is saved as the merchant's backend: it did
/// not answer, refused the password, or was given a password that can spend. Controllers answer it with
/// HTTP 422 and the message as the problem detail.
/// </summary>
public class LightningBackendCheckException(string message) : Exception(message);
