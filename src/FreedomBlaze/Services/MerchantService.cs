using FreedomBlaze.Data.Entities;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>
/// Default <see cref="IMerchantService"/>. Merchants are never deleted: their invoices reference them,
/// so retiring one means deactivating it, which stops its keys from authenticating.
/// </summary>
public sealed class MerchantService(
    IRepository<Merchant> merchants,
    TimeProvider timeProvider,
    ILogger<MerchantService> logger) : IMerchantService
{
    public async Task<MerchantInfo> CreateAsync(CreateMerchantRequest request, CancellationToken cancellationToken)
    {
        var merchant = new Merchant
        {
            Name = RequireName(request.Name),
            IsActive = true,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };

        await merchants.AddAsync(merchant, cancellationToken);
        logger.LogInformation("Registered merchant {MerchantId} '{Name}'.", merchant.Id, merchant.Name);
        return ToInfo(merchant);
    }

    public async Task<IReadOnlyList<MerchantInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var all = await merchants.ListAsync(cancellationToken);
        return all.OrderBy(m => m.Id).Select(ToInfo).ToList();
    }

    public async Task<MerchantInfo?> GetAsync(int id, CancellationToken cancellationToken) =>
        await merchants.GetByIdAsync(id, cancellationToken) is { } merchant ? ToInfo(merchant) : null;

    public async Task<MerchantInfo?> UpdateAsync(int id, UpdateMerchantRequest request, CancellationToken cancellationToken)
    {
        var merchant = await merchants.GetByIdAsync(id, cancellationToken);
        if (merchant is null)
            return null;

        if (request.Name is not null)
            merchant.Name = RequireName(request.Name);

        if (request.IsActive is { } isActive && isActive != merchant.IsActive)
        {
            merchant.IsActive = isActive;
            merchant.DeactivatedAtUtc = isActive ? null : timeProvider.GetUtcNow();
            logger.LogInformation("Merchant {MerchantId} '{Name}' {State}.",
                merchant.Id, merchant.Name, isActive ? "reactivated" : "deactivated");
        }

        await merchants.UpdateAsync(merchant, cancellationToken);
        return ToInfo(merchant);
    }

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new RequestValidationException("A merchant needs a name.")
            : name.Trim();

    private static MerchantInfo ToInfo(Merchant merchant) => new()
    {
        Id = merchant.Id,
        Name = merchant.Name,
        IsActive = merchant.IsActive,
        CreatedAtUtc = merchant.CreatedAtUtc,
        DeactivatedAtUtc = merchant.DeactivatedAtUtc,
    };
}
