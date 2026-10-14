using System.Net;

namespace FreedomBlaze.Services.Lightning;

/// <summary>What the gateway asks a node to issue.</summary>
public sealed record BackendInvoiceRequest(
    long AmountSat, string Description, string ExternalId, int ExpirySeconds, string? WebhookUrl);

/// <summary>An invoice as the node issued it.</summary>
public sealed record BackendInvoice(string PaymentHash, string Bolt11);

/// <summary>
/// An incoming payment as the node reports it. <see cref="Preimage"/> must never be logged or stored
/// while the invoice is unpaid: whoever knows it could settle the payment in transit.
/// </summary>
public sealed record BackendPayment(
    bool IsPaid,
    bool IsExpired,
    long? RequestedSat,
    long ReceivedSat,
    long FeesMsat,
    string? Preimage,
    DateTimeOffset? CompletedAt);

/// <summary>Who the node says it is.</summary>
public sealed record BackendNodeInfo(string NodeId, string Chain, string Version, int Channels);

/// <summary>
/// A Lightning node that issues and tracks a merchant's invoices. Receive-only by design: nothing here
/// can move funds.
/// </summary>
public interface ILightningBackend
{
    /// <exception cref="LightningBackendException">The node refused or could not be reached.</exception>
    Task<BackendInvoice> CreateInvoiceAsync(BackendInvoiceRequest request, CancellationToken cancellationToken);

    /// <summary>The payment for <paramref name="paymentHash"/>, or null when the node does not know it.</summary>
    /// <exception cref="LightningBackendException">The node refused or could not be reached.</exception>
    Task<BackendPayment?> FindIncomingPaymentAsync(string paymentHash, CancellationToken cancellationToken);

    /// <exception cref="LightningBackendException">The node refused or could not be reached.</exception>
    Task<BackendNodeInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True when the gateway's credential could also spend from the node. Merchants' nodes must be given
    /// a receive-only credential, so this is checked before one is stored.
    /// </summary>
    /// <exception cref="LightningBackendException">The node's answer settles nothing either way.</exception>
    Task<bool> CanSpendAsync(CancellationToken cancellationToken);
}

/// <summary>A node refused a request or could not be reached. The message is safe to show the operator.</summary>
public sealed class LightningBackendException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>The node rejected the password.</summary>
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// A stored backend cannot be used on this server: the gateway's own node is not configured, or a
/// merchant's password can no longer be decrypted.
/// </summary>
public sealed class LightningBackendUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
