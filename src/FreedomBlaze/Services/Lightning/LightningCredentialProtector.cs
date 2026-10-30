using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services.Lightning;

/// <summary>What a protected secret is, so one kind can never be decrypted as another.</summary>
public enum CredentialPurpose
{
    /// <summary>A merchant node's password.</summary>
    NodePassword,

    /// <summary>A webhook endpoint's signing secret.</summary>
    WebhookSecret,
}

/// <summary>Encrypts the secrets the gateway stores: merchants' node passwords and webhook signing secrets.</summary>
public interface ILightningCredentialProtector
{
    /// <summary>True when a key is configured, so a secret can be stored.</summary>
    bool CanProtect { get; }

    /// <exception cref="InvalidOperationException">No credential key is configured.</exception>
    string Protect(int merchantId, string secret, CredentialPurpose purpose = CredentialPurpose.NodePassword);

    /// <exception cref="CryptographicException">
    /// The value was altered, was encrypted for another merchant or purpose, or needs a key this server no
    /// longer has.
    /// </exception>
    string Unprotect(int merchantId, string protectedSecret, CredentialPurpose purpose = CredentialPurpose.NodePassword);
}

/// <summary>
/// AES-256-GCM under keys from configuration, never from the database, so a copy of the database alone
/// reveals no secret. Each value names the key that encrypted it, so keys can rotate, and is bound to its
/// merchant and purpose, so it cannot be moved to another merchant's row or read as another kind of secret.
/// </summary>
public sealed partial class AesGcmCredentialProtector : ILightningCredentialProtector
{
    private const string Format = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string? _activeKeyId;

    public AesGcmCredentialProtector(IOptions<LightningBackendOptions> options)
    {
        var settings = options.Value;
        if (Validate(settings) is { } problem)
            throw new InvalidOperationException(problem);

        _keys = settings.CredentialKeys.ToDictionary(k => k.Key, k => Convert.FromBase64String(k.Value), StringComparer.Ordinal);
        _activeKeyId = ActiveKeyId(settings);
    }

    public bool CanProtect => _activeKeyId is not null;

    public string Protect(int merchantId, string secret, CredentialPurpose purpose = CredentialPurpose.NodePassword)
    {
        if (_activeKeyId is not { } keyId)
            throw new InvalidOperationException("No credential key is configured (LightningBackends:CredentialKeys).");

        var plaintext = Encoding.UTF8.GetBytes(secret);
        var payload = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_keys[keyId], TagSize);
        aes.Encrypt(nonce, plaintext, payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize),
            AssociatedData(merchantId, purpose));

        return $"{Format}:{keyId}:{Convert.ToBase64String(payload)}";
    }

    public string Unprotect(int merchantId, string protectedSecret, CredentialPurpose purpose = CredentialPurpose.NodePassword)
    {
        if (protectedSecret.Split(':') is not [Format, var keyId, var encoded])
            throw new CryptographicException("Not a protected secret.");

        if (!_keys.TryGetValue(keyId, out var key))
            throw new CryptographicException($"Credential key '{keyId}' is not configured.");

        var payload = new byte[(encoded.Length * 3 + 3) / 4];
        if (!Convert.TryFromBase64String(encoded, payload, out var length) || length < NonceSize + TagSize)
            throw new CryptographicException("Not a protected secret.");

        var plaintext = new byte[length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize + TagSize, plaintext.Length),
            payload.AsSpan(NonceSize, TagSize), plaintext, AssociatedData(merchantId, purpose));

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>What is wrong with the configured keys, or null when nothing is. Run at startup.</summary>
    public static string? Validate(LightningBackendOptions settings)
    {
        foreach (var (id, value) in settings.CredentialKeys)
        {
            if (!KeyIdPattern().IsMatch(id))
                return $"Credential key id '{id}' may only use letters, digits, '-' and '_'.";

            var key = new byte[(value.Length * 3 + 3) / 4];
            if (!Convert.TryFromBase64String(value, key, out var length) || length != 32)
                return $"Credential key '{id}' must be 32 random bytes in base64 (AES-256).";
        }

        if (settings.ActiveCredentialKeyId is { Length: > 0 } active && !settings.CredentialKeys.ContainsKey(active))
            return $"LightningBackends:ActiveCredentialKeyId '{active}' is not one of LightningBackends:CredentialKeys.";

        if (settings.CredentialKeys.Count > 1 && string.IsNullOrEmpty(settings.ActiveCredentialKeyId))
            return "Several credential keys are configured; name the one to use in LightningBackends:ActiveCredentialKeyId.";

        return null;
    }

    private static string? ActiveKeyId(LightningBackendOptions settings) =>
        settings.ActiveCredentialKeyId is { Length: > 0 } active ? active
        : settings.CredentialKeys.Count == 1 ? settings.CredentialKeys.Keys.Single()
        : null;

    private static byte[] AssociatedData(int merchantId, CredentialPurpose purpose) => Encoding.UTF8.GetBytes(purpose switch
    {
        CredentialPurpose.NodePassword => $"FreedomBlaze/LightningBackend/password/merchant:{merchantId}",
        CredentialPurpose.WebhookSecret => $"FreedomBlaze/WebhookEndpoint/secret/merchant:{merchantId}",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    });

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex KeyIdPattern();
}
