using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>Manages the node each merchant's invoices are issued on.</summary>
public interface ILightningBackendService
{
    /// <summary>The merchant's current backend, or null when it has none.</summary>
    Task<LightningBackendInfo?> GetAsync(int merchantId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the node and makes it the merchant's backend, retiring the previous one. Nothing is stored
    /// unless the node answers, and a merchant's own node must be given a password that cannot spend.
    /// The node's public key is pinned: invoices it does not sign are refused from then on.
    /// </summary>
    /// <exception cref="Exceptions.RequestValidationException">The settings are malformed.</exception>
    /// <exception cref="Exceptions.LightningBackendCheckException">The node failed the check.</exception>
    Task<LightningBackendInfo> ConfigureAsync(int merchantId, ConfigureLightningBackendRequest request, CancellationToken cancellationToken);

    /// <summary>Checks the current backend's node now and records the result; null when there is none.</summary>
    Task<LightningBackendInfo?> CheckAsync(int merchantId, CancellationToken cancellationToken);

    /// <summary>
    /// Retires the merchant's backend: the merchant can no longer issue invoices, but the ones it issued
    /// are still read through it. False when there was none.
    /// </summary>
    Task<bool> RemoveAsync(int merchantId, CancellationToken cancellationToken);
}
