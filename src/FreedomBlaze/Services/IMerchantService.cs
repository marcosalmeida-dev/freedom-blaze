using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>Registers and manages the merchants the gateway takes payments for.</summary>
public interface IMerchantService
{
    Task<MerchantInfo> CreateAsync(CreateMerchantRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The merchant, or null when no such merchant exists.</summary>
    Task<MerchantInfo?> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Renames and/or (de)activates a merchant. Returns null when no such merchant exists.
    /// </summary>
    /// <exception cref="Exceptions.RequestValidationException">The new name is blank.</exception>
    Task<MerchantInfo?> UpdateAsync(int id, UpdateMerchantRequest request, CancellationToken cancellationToken);
}
