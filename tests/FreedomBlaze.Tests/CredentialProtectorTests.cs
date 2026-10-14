using System.Security.Cryptography;
using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;

namespace FreedomBlaze.Tests;

public sealed class CredentialProtectorTests
{
    [Fact]
    public void APasswordRoundTripsWithoutEverBeingStoredInClear()
    {
        var protector = TestCredentials.Protector();

        var first = protector.Protect(7, "limited-password");
        var second = protector.Protect(7, "limited-password");

        Assert.StartsWith("v1:k1:", first);
        Assert.DoesNotContain("limited-password", first);
        Assert.NotEqual(first, second); // a fresh nonce every time
        Assert.Equal("limited-password", protector.Unprotect(7, first));
        Assert.Equal("limited-password", protector.Unprotect(7, second));
    }

    [Fact]
    public void AProtectedPasswordOnlyOpensForItsMerchant()
    {
        var protector = TestCredentials.Protector();
        var stored = protector.Protect(7, "limited-password");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(8, stored));
    }

    [Fact]
    public void AnAlteredValueIsRefused()
    {
        var protector = TestCredentials.Protector();
        var stored = protector.Protect(7, "limited-password");
        var payload = Convert.FromBase64String(stored["v1:k1:".Length..]);
        payload[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(7, "v1:k1:" + Convert.ToBase64String(payload)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(7, "plain-text"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(7, "v1:k1:not base64!"));
    }

    [Fact]
    public void KeysRotateWithoutLosingWhatTheOldKeyEncrypted()
    {
        var oldKey = TestCredentials.NewKey();
        var before = Protector(new() { CredentialKeys = { ["k1"] = oldKey } });
        var stored = before.Protect(7, "limited-password");

        var after = Protector(new()
        {
            CredentialKeys = { ["k1"] = oldKey, ["k2"] = TestCredentials.NewKey() },
            ActiveCredentialKeyId = "k2",
        });
        var withoutOldKey = Protector(new() { CredentialKeys = { ["k2"] = TestCredentials.NewKey() } });

        Assert.Equal("limited-password", after.Unprotect(7, stored));
        Assert.StartsWith("v1:k2:", after.Protect(7, "limited-password"));
        var missing = Assert.ThrowsAny<CryptographicException>(() => withoutOldKey.Unprotect(7, stored));
        Assert.Contains("'k1' is not configured", missing.Message);
    }

    [Fact]
    public void WithoutAKeyNothingCanBeStored()
    {
        var protector = Protector(new());

        Assert.False(protector.CanProtect);
        Assert.Throws<InvalidOperationException>(() => protector.Protect(7, "limited-password"));
    }

    [Theory]
    [InlineData("k1", "c2hvcnQ=", null, "must be 32 random bytes")]
    [InlineData("k1", "not base64", null, "must be 32 random bytes")]
    [InlineData("k:1", null, null, "may only use letters")]
    [InlineData("k1", null, "k9", "is not one of")]
    public void MalformedKeysStopTheServerFromStarting(string id, string? value, string? active, string problem)
    {
        var settings = new LightningBackendOptions
        {
            CredentialKeys = { [id] = value ?? TestCredentials.NewKey() },
            ActiveCredentialKeyId = active,
        };

        var result = new LightningBackendOptionsValidator().Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains(problem, result.FailureMessage);
        Assert.Throws<InvalidOperationException>(() => Protector(settings));
    }

    [Fact]
    public void SeveralKeysNeedTheActiveOneNamed()
    {
        var settings = new LightningBackendOptions
        {
            CredentialKeys = { ["k1"] = TestCredentials.NewKey(), ["k2"] = TestCredentials.NewKey() },
        };

        Assert.Contains("ActiveCredentialKeyId", new LightningBackendOptionsValidator().Validate(null, settings).FailureMessage);
        Assert.True(new LightningBackendOptionsValidator().Validate(null, new LightningBackendOptions()).Succeeded);
    }

    private static AesGcmCredentialProtector Protector(LightningBackendOptions settings) =>
        new(Microsoft.Extensions.Options.Options.Create(settings));
}
