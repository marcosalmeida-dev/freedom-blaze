using System.ComponentModel.DataAnnotations;
using FreedomBlaze.Data.Entities;

namespace FreedomBlaze.Models.Api;

/// <summary>
/// Sets the node a merchant's invoices are issued on: the merchant's own <c>phoenixd</c>, reached at
/// <see cref="Url"/> with its limited-access password, or the gateway's own node (<c>operator</c>).
/// </summary>
public sealed class ConfigureLightningBackendRequest
{
    /// <summary><c>phoenixd</c> or <c>operator</c>.</summary>
    [Required]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The node's HTTPS base URL (phoenixd only), e.g. <c>https://node.example.com</c>.</summary>
    [StringLength(2048)]
    public string? Url { get; set; }

    /// <summary>
    /// phoenixd's <c>http-password-limited-access</c> from <c>phoenix.conf</c> (phoenixd only). The
    /// full-access <c>http-password</c> is refused: the gateway must not be able to spend the merchant's funds.
    /// </summary>
    [StringLength(256)]
    public string? Password { get; set; }
}

/// <summary>A merchant's Lightning backend as the management API shows it. The password is never returned.</summary>
public sealed class LightningBackendInfo
{
    public const string OperatorKind = "operator";
    public const string PhoenixdKind = "phoenixd";

    public int Id { get; init; }

    /// <summary><c>phoenixd</c> (the merchant's own node) or <c>operator</c> (the gateway's node).</summary>
    public string Kind { get; init; } = string.Empty;

    public string? Url { get; init; }

    /// <summary>Whether an encrypted password is stored.</summary>
    public bool HasPassword { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? RetiredAtUtc { get; init; }

    /// <summary>The pinned node: every invoice must carry its signature.</summary>
    public string? NodeId { get; init; }

    public string? Chain { get; init; }

    public DateTimeOffset? CheckedAtUtc { get; init; }

    public bool? Reachable { get; init; }

    public string? CheckError { get; init; }

    public static LightningBackendInfo From(LightningBackend backend) => new()
    {
        Id = backend.Id,
        Kind = KindName(backend.Kind),
        Url = backend.Url,
        HasPassword = backend.ProtectedPassword is not null,
        IsActive = backend.IsActive,
        CreatedAtUtc = backend.CreatedAtUtc,
        RetiredAtUtc = backend.RetiredAtUtc,
        NodeId = backend.NodeId,
        Chain = backend.Chain,
        CheckedAtUtc = backend.CheckedAtUtc,
        Reachable = backend.Reachable,
        CheckError = backend.CheckError,
    };

    public static string KindName(LightningBackendKind kind) => kind switch
    {
        LightningBackendKind.OperatorNode => OperatorKind,
        LightningBackendKind.Phoenixd => PhoenixdKind,
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Parses a wire kind name, case-insensitively.</summary>
    public static bool TryParseKind(string? name, out LightningBackendKind kind)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case OperatorKind:
                kind = LightningBackendKind.OperatorNode;
                return true;
            case PhoenixdKind:
                kind = LightningBackendKind.Phoenixd;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
