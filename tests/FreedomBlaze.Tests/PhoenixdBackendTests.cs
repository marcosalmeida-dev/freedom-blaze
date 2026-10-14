using System.Net;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Services.Lightning;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Tests;

/// <summary>The receive-only phoenixd client and the factory that points it at a merchant's node.</summary>
public sealed class PhoenixdBackendTests
{
    private const string NodeUrl = "https://node.example/";

    [Fact]
    public async Task IssuesLooksUpAndDescribes()
    {
        using var network = new FakeLightningNetwork();
        var node = network.Add(NodeUrl, new FakePhoenixd("limited"));
        var backend = Factory(network).CreatePhoenixd(new Uri(NodeUrl), "limited");

        var info = await backend.GetInfoAsync(default);
        var issued = await backend.CreateInvoiceAsync(new BackendInvoiceRequest(10_000, "Pedido 123", "m1:order-123", 600, null), default);
        node.Pay(issued.PaymentHash);
        var paid = await backend.FindIncomingPaymentAsync(issued.PaymentHash, default);
        var unknown = await backend.FindIncomingPaymentAsync(new string('f', 64), default);

        Assert.Equal(node.NodeId, info.NodeId);
        Assert.Equal("mainnet", info.Chain);
        Assert.Equal("0.9.2", info.Version);
        Assert.Equal(node[issued.PaymentHash].Bolt11, issued.Bolt11);
        Assert.True(paid!.IsPaid);
        Assert.Equal(node[issued.PaymentHash].Preimage, paid.Preimage);
        Assert.Equal(10_000, paid.ReceivedSat);
        Assert.Equal(21_000_000, paid.FeesMsat);
        Assert.Null(unknown);
        Assert.Equal(new[] { "/getinfo", "/createinvoice", $"/payments/incoming/{issued.PaymentHash}", $"/payments/incoming/{new string('f', 64)}" },
            node.Requests.Select(r => r.Path).ToArray());
    }

    [Fact]
    public async Task ANodeBehindAPathPrefixIsReachedThere()
    {
        using var network = new FakeLightningNetwork();
        var node = network.Add("https://proxy.example/", new FakePhoenixd("limited"));

        await Factory(network).CreatePhoenixd(new Uri("https://proxy.example/phoenixd"), "limited").GetInfoAsync(default);

        Assert.Equal("/phoenixd/getinfo", Assert.Single(node.Requests).Path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task OnlyTheFullAccessPasswordGetsPastTheSpendingDoor(bool fullAccess, bool canSpend)
    {
        using var network = new FakeLightningNetwork();
        var node = network.Add(NodeUrl, new FakePhoenixd("password", fullAccess));

        var answer = await Factory(network).CreatePhoenixd(new Uri(NodeUrl), "password").CanSpendAsync(default);

        Assert.Equal(canSpend, answer);
        var probe = Assert.Single(node.Requests);
        Assert.Equal(("POST", "/lnurlauth", ""), (probe.Method, probe.Path, probe.Body)); // no lnurl: nothing to sign
    }

    [Fact]
    public async Task AWrongPasswordIsAnAuthenticationFailure()
    {
        using var network = new FakeLightningNetwork();
        network.Add(NodeUrl, new FakePhoenixd("limited"));

        var refused = await Assert.ThrowsAsync<LightningBackendException>(() =>
            Factory(network).CreatePhoenixd(new Uri(NodeUrl), "wrong").GetInfoAsync(default));

        Assert.True(refused.IsAuthenticationFailure);
        Assert.StartsWith("The node refused getinfo: HTTP 401 Invalid authentication", refused.Message);
    }

    [Fact]
    public async Task UnreachableSlowAndStrangeNodesAreReportedPlainly()
    {
        using var network = new FakeLightningNetwork();
        var node = network.Add(NodeUrl, new FakePhoenixd("limited"));
        var backend = Factory(network).CreatePhoenixd(new Uri(NodeUrl), "limited");

        var unreachable = await Assert.ThrowsAsync<LightningBackendException>(() =>
            Factory(network).CreatePhoenixd(new Uri("https://nowhere.example/"), "limited").GetInfoAsync(default));
        node.Override = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var slow = await Assert.ThrowsAsync<LightningBackendException>(() => backend.GetInfoAsync(default));
        node.Override = _ => FakePhoenixd.Text(HttpStatusCode.OK, "<html>not phoenixd</html>");
        var strange = await Assert.ThrowsAsync<LightningBackendException>(() => backend.GetInfoAsync(default));

        Assert.StartsWith("The node could not be reached (getinfo)", unreachable.Message);
        Assert.Equal("The node did not answer getinfo in time.", slow.Message);
        Assert.Equal("The node's answer to getinfo is not phoenixd's.", strange.Message);
    }

    [Fact]
    public async Task ACancelledCallerIsNotMistakenForASlowNode()
    {
        using var network = new FakeLightningNetwork();
        network.Add(NodeUrl, new FakePhoenixd("limited"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Factory(network).CreatePhoenixd(new Uri(NodeUrl), "limited").GetInfoAsync(cancelled.Token));
    }

    [Fact]
    public async Task TheOperatorsNodeIsReachedWithItsLimitedPasswordWhenThereIsOne()
    {
        using var network = new FakeLightningNetwork();
        network.Add("https://operator.node/", new FakePhoenixd("operator-limited"));
        var withLimited = Factory(network, new PhoenixConfig { Host = "https://operator.node", Token = "operator-full", LimitedAccessToken = "operator-limited" });
        var fullOnly = Factory(network, new PhoenixConfig { Host = "https://operator.node", Token = "operator-limited" });
        var operatorBackend = new LightningBackend { MerchantId = 1, Kind = LightningBackendKind.OperatorNode };

        Assert.NotNull(await withLimited.Create(operatorBackend).GetInfoAsync(default));
        Assert.NotNull(await fullOnly.Create(operatorBackend).GetInfoAsync(default)); // falls back to Token
        var missing = Assert.Throws<LightningBackendUnavailableException>(() => Factory(network).Create(operatorBackend));
        Assert.Contains("PhoenixConfig:Host", missing.Message);
    }

    [Fact]
    public async Task AMerchantsNodeIsReachedWithItsOwnDecryptedPassword()
    {
        using var network = new FakeLightningNetwork();
        network.Add(NodeUrl, new FakePhoenixd("limited"));
        var protector = TestCredentials.Protector();
        var factory = new LightningBackendFactory(network, protector);
        var backend = new LightningBackend
        {
            MerchantId = 7, Kind = LightningBackendKind.Phoenixd, Url = NodeUrl, ProtectedPassword = protector.Protect(7, "limited"),
        };
        var moved = new LightningBackend
        {
            MerchantId = 8, Kind = LightningBackendKind.Phoenixd, Url = NodeUrl, ProtectedPassword = backend.ProtectedPassword,
        };

        Assert.NotNull(await factory.Create(backend).GetInfoAsync(default));
        var refused = Assert.Throws<LightningBackendUnavailableException>(() => factory.Create(moved));
        Assert.Contains("cannot be decrypted", refused.Message);
    }

    private static LightningBackendFactory Factory(FakeLightningNetwork network, PhoenixConfig? operatorNode = null) =>
        new(network, TestCredentials.Protector(), operatorNode);
}
