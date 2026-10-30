using FreedomBlaze.Services.Webhooks;

namespace FreedomBlaze.Tests;

public sealed class WebhookSignatureTests
{
    [Fact]
    public void SignsKnownMessageWithSyntheticTestKey()
    {
        // Synthetic bytes 0..31, never a provider-issued credential.
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var secret = WebhookSignature.SecretPrefix + Convert.ToBase64String(key);
        var signature = WebhookSignature.Sign(secret, "msg_p5jXN8AQM9LWM0D4loKWxJek",
            1614265330, "{\"test\": 2432232314}");

        // Fixed HMAC-SHA256 result computed independently of WebhookSignature.
        Assert.Equal("v1,O4Gjv1HqPqsMrjmczoggs/sWA8gZD0VyHG+fLh4+ktI=", signature);
    }

    [Fact]
    public void OnlyTheExactMessageVerifies()
    {
        var secret = WebhookSignature.NewSecret();
        var signature = WebhookSignature.Sign(secret, "evt_1", 1_700_000_000, "{\"a\":1}");

        Assert.True(WebhookSignature.Verify(secret, "evt_1", 1_700_000_000, "{\"a\":1}", signature));
        Assert.True(WebhookSignature.Verify(secret, "evt_1", 1_700_000_000, "{\"a\":1}", "v1,c29tZXRoaW5nIGVsc2U= " + signature));
        Assert.False(WebhookSignature.Verify(secret, "evt_1", 1_700_000_000, "{\"a\":2}", signature));
        Assert.False(WebhookSignature.Verify(secret, "evt_2", 1_700_000_000, "{\"a\":1}", signature));
        Assert.False(WebhookSignature.Verify(secret, "evt_1", 1_700_000_001, "{\"a\":1}", signature));
        Assert.False(WebhookSignature.Verify(WebhookSignature.NewSecret(), "evt_1", 1_700_000_000, "{\"a\":1}", signature));
    }

    [Fact]
    public void SecretsAreThirtyTwoFreshRandomBytes()
    {
        var first = WebhookSignature.NewSecret();
        var second = WebhookSignature.NewSecret();

        Assert.StartsWith(WebhookSignature.SecretPrefix, first);
        Assert.Equal(32, Convert.FromBase64String(first[WebhookSignature.SecretPrefix.Length..]).Length);
        Assert.NotEqual(first, second);
    }
}
