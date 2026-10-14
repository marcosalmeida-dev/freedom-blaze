using FreedomBlaze.Options;
using FreedomBlaze.Services.Lightning;

namespace FreedomBlaze.Tests;

internal static class TestCredentials
{
    public static string NewKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>A protector with one fresh key, as a configured server has.</summary>
    public static AesGcmCredentialProtector Protector() =>
        new(Microsoft.Extensions.Options.Options.Create(new LightningBackendOptions { CredentialKeys = { ["k1"] = NewKey() } }));
}
