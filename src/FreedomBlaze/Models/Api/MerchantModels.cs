using System.ComponentModel.DataAnnotations;

namespace FreedomBlaze.Models.Api;

/// <summary>Request to register a merchant.</summary>
public sealed class CreateMerchantRequest
{
    /// <summary>Display name, e.g. "Artcana".</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Changes to a merchant; omitted fields are left as they are.</summary>
public sealed class UpdateMerchantRequest
{
    [StringLength(128, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>False deactivates the merchant: its keys stop authenticating. True reactivates it.</summary>
    public bool? IsActive { get; set; }
}

/// <summary>A merchant as the management API shows it.</summary>
public sealed class MerchantInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? DeactivatedAtUtc { get; init; }
}
