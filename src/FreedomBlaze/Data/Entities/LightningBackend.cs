namespace FreedomBlaze.Data.Entities;

/// <summary>What kind of Lightning node issues a merchant's invoices.</summary>
public enum LightningBackendKind
{
    /// <summary>
    /// The gateway operator's own phoenixd (<c>PhoenixConfig</c>). Payments land in the operator's wallet,
    /// so only the operator's own businesses should use it.
    /// </summary>
    OperatorNode = 0,

    /// <summary>
    /// The merchant's own phoenixd, reached with its limited-access password, which can issue and read
    /// invoices but never send. Payments land in the merchant's wallet.
    /// </summary>
    Phoenixd = 1,
}

/// <summary>
/// Where a merchant's invoices are issued. A merchant has at most one active backend. Replacing it retires
/// the old row instead of deleting it, because the invoices it issued are still read through it.
/// </summary>
public class LightningBackend
{
    public int Id { get; set; }

    public int MerchantId { get; set; }

    public LightningBackendKind Kind { get; set; }

    /// <summary>Base URL of the merchant's node, ending in <c>/</c>; null for the operator's node.</summary>
    public string? Url { get; set; }

    /// <summary>The node's password, encrypted by the credential protector; null for the operator's node.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>The merchant's current backend. A retired backend only serves the invoices it issued.</summary>
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? RetiredAtUtc { get; set; }

    /// <summary>
    /// The node's public key, pinned when the backend is configured. Every invoice must carry this node's
    /// signature, so a different node answering at the same URL cannot take the merchant's payments.
    /// </summary>
    public string? NodeId { get; set; }

    /// <summary>The chain the node reported, e.g. <c>mainnet</c>.</summary>
    public string? Chain { get; set; }

    /// <summary>When the node was last checked.</summary>
    public DateTimeOffset? CheckedAtUtc { get; set; }

    /// <summary>Whether the last check reached the pinned node.</summary>
    public bool? Reachable { get; set; }

    /// <summary>Why the last check failed; null after a successful one.</summary>
    public string? CheckError { get; set; }
}
