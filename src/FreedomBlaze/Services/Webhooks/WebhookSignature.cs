using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FreedomBlaze.Services.Webhooks;

/// <summary>
/// Signs webhooks the way the Standard Webhooks specification does (standardwebhooks.com), so receivers can
/// use its libraries: <c>webhook-signature: v1,base64(HMAC-SHA256(key, "{id}.{timestamp}.{body}"))</c>, where
/// the key is the base64 part of a <c>whsec_</c> secret.
/// </summary>
public static class WebhookSignature
{
    public const string SecretPrefix = "whsec_";
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    /// <summary>A new secret: 32 random bytes.</summary>
    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, string messageId, long timestamp, string payload)
    {
        var content = Encoding.UTF8.GetBytes($"{messageId}.{timestamp.ToString(CultureInfo.InvariantCulture)}.{payload}");
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(Key(secret), content));
    }

    /// <summary>
    /// Whether <paramref name="signatureHeader"/> (one or more space-separated signatures) holds a valid
    /// signature of the message. What a receiver does, also before trusting the timestamp's freshness.
    /// </summary>
    public static bool Verify(string secret, string messageId, long timestamp, string payload, string signatureHeader)
    {
        var expected = Encoding.ASCII.GetBytes(Sign(secret, messageId, timestamp, payload));
        return signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), expected));
    }

    private static byte[] Key(string secret) =>
        Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret);
}
