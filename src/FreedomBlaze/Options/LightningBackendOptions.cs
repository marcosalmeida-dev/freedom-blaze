namespace FreedomBlaze.Options;

/// <summary>
/// How the gateway reaches merchants' Lightning nodes and protects their passwords, bound from the
/// "LightningBackends" section.
/// </summary>
public sealed class LightningBackendOptions
{
    public const string Section = "LightningBackends";

    /// <summary>
    /// AES-256 keys (base64, 32 bytes) that encrypt node passwords, by key id. Keep them out of the
    /// database: an environment variable or secret file. Keep a retired key listed until every password
    /// it encrypted has been configured again, or those backends stop working.
    /// </summary>
    public Dictionary<string, string> CredentialKeys { get; set; } = [];

    /// <summary>The key id new passwords are encrypted with. Optional when only one key is listed.</summary>
    public string? ActiveCredentialKeyId { get; set; }

    /// <summary>
    /// Accept <c>http://</c> node URLs. Only for local development: a node password must not cross a
    /// network in clear text.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>How long a node has to answer one request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}
