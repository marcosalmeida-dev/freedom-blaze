using FreedomBlaze.Data.Entities;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using FreedomBlaze.Services.Lightning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Tests;

public sealed class LightningBackendServiceTests
{
    private const string NodeUrl = "https://node.artcana.example/";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMerchantsOwnNodeIsCheckedPinnedAndStoredWithItsPasswordEncrypted()
    {
        using var fixture = new Fixture();

        var info = await fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd("https://node.artcana.example", "limited"), default);

        Assert.Equal(LightningBackendInfo.PhoenixdKind, info.Kind);
        Assert.Equal(NodeUrl, info.Url);
        Assert.True(info.HasPassword);
        Assert.True(info.IsActive);
        Assert.Equal(fixture.Node.NodeId, info.NodeId);
        Assert.Equal("mainnet", info.Chain);
        Assert.True(info.Reachable);
        Assert.Equal(Now, info.CheckedAtUtc);

        var stored = Assert.Single(fixture.Backends());
        Assert.DoesNotContain("limited", stored.ProtectedPassword);
        Assert.Equal("limited", fixture.Protector.Unprotect(fixture.MerchantId, stored.ProtectedPassword!));
        Assert.Equal(new[] { "/getinfo", "/lnurlauth" }, fixture.Node.Requests.Select(r => r.Path).ToArray());
        Assert.Equal(info.Id, (await fixture.Service.GetAsync(fixture.MerchantId, default))!.Id);
    }

    [Fact]
    public async Task APasswordThatCanSpendIsRefused()
    {
        using var fixture = new Fixture(fullAccess: true);

        var refused = await Assert.ThrowsAsync<LightningBackendCheckException>(() =>
            fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(NodeUrl, "limited"), default));

        Assert.Contains("http-password-limited-access", refused.Message);
        Assert.Empty(fixture.Backends());
    }

    [Theory]
    [InlineData("https://node.artcana.example/", "wrong", "The node refused the password.")]
    [InlineData("https://elsewhere.example/", "limited", "The node could not be reached (getinfo)")]
    public async Task ANodeThatDoesNotAnswerIsNotSaved(string url, string password, string problem)
    {
        using var fixture = new Fixture();

        var refused = await Assert.ThrowsAsync<LightningBackendCheckException>(() =>
            fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(url, password), default));

        Assert.StartsWith(problem, refused.Message);
        Assert.Empty(fixture.Backends());
    }

    [Theory]
    [InlineData("http://node.artcana.example", "must use HTTPS")]
    [InlineData("https://user:pw@node.artcana.example", "must not embed credentials")]
    [InlineData("https://node.artcana.example/?token=1", "must not have a query")]
    [InlineData("node.artcana.example", "absolute HTTPS URL")]
    [InlineData(null, "absolute HTTPS URL")]
    public async Task UnsafeUrlsAreRefusedBeforeAnyRequest(string? url, string problem)
    {
        using var fixture = new Fixture();

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() =>
            fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(url, "limited"), default));

        Assert.Contains(problem, refused.Message);
        Assert.Empty(fixture.Node.Requests);
    }

    [Fact]
    public async Task PlainHttpIsForLocalDevelopmentOnly()
    {
        using var fixture = new Fixture(allowInsecureHttp: true);
        fixture.Network.Add("http://127.0.0.1:9799/", fixture.Node);

        var info = await fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd("http://127.0.0.1:9799", "limited"), default);

        Assert.Equal("http://127.0.0.1:9799/", info.Url);
    }

    [Theory]
    [InlineData("lnd", null, null, "kind must be")]
    [InlineData("phoenixd", NodeUrl, null, "password is required")]
    [InlineData("operator", NodeUrl, null, "takes no url or password")]
    public async Task MalformedRequestsAreRefused(string kind, string? url, string? password, string problem)
    {
        using var fixture = new Fixture();

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.ConfigureAsync(fixture.MerchantId,
            new ConfigureLightningBackendRequest { Kind = kind, Url = url, Password = password }, default));

        Assert.Contains(problem, refused.Message);
    }

    [Fact]
    public async Task WithoutACredentialKeyNoPasswordCanBeStored()
    {
        using var fixture = new Fixture(credentialKey: false);

        var refused = await Assert.ThrowsAsync<RequestValidationException>(() =>
            fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(NodeUrl, "limited"), default));

        Assert.Contains("LightningBackends:CredentialKeys", refused.Message);
        Assert.Empty(fixture.Node.Requests);
    }

    [Fact]
    public async Task TheOperatorsNodeCanServeAMerchantOnlyWhereItIsConfigured()
    {
        using var configured = new Fixture();
        using var missing = new Fixture(operatorNode: false);
        var request = new ConfigureLightningBackendRequest { Kind = "operator" };

        var info = await configured.Service.ConfigureAsync(configured.MerchantId, request, default);
        var refused = await Assert.ThrowsAsync<LightningBackendCheckException>(() =>
            missing.Service.ConfigureAsync(missing.MerchantId, request, default));

        Assert.Equal(LightningBackendInfo.OperatorKind, info.Kind);
        Assert.False(info.HasPassword);
        Assert.Null(info.Url);
        Assert.Equal(configured.Operator.NodeId, info.NodeId);
        Assert.DoesNotContain(configured.Operator.Requests, r => r.Path == "/lnurlauth"); // the operator may spend from its own node
        Assert.Contains("PhoenixConfig:Host", refused.Message);
    }

    [Fact]
    public async Task ReplacingABackendRetiresTheOldOneInsteadOfDeletingIt()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.ConfigureAsync(fixture.MerchantId, new ConfigureLightningBackendRequest { Kind = "operator" }, default);
        fixture.Clock.Now = Now.AddDays(1);

        var second = await fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(NodeUrl, "limited"), default);

        var backends = fixture.Backends();
        Assert.Equal(2, backends.Count);
        var retired = backends.Single(b => b.Id == first.Id);
        Assert.False(retired.IsActive);
        Assert.Equal(Now.AddDays(1), retired.RetiredAtUtc);
        Assert.True(backends.Single(b => b.Id == second.Id).IsActive);
        Assert.Equal(second.Id, (await fixture.Service.GetAsync(fixture.MerchantId, default))!.Id);
    }

    [Fact]
    public async Task ACheckRecordsWhetherThePinnedNodeStillAnswers()
    {
        using var fixture = new Fixture();
        var configured = await fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(NodeUrl, "limited"), default);
        fixture.Clock.Now = Now.AddHours(1);

        var healthy = await fixture.Service.CheckAsync(fixture.MerchantId, default);
        var pinned = fixture.Node.NodeKey;
        fixture.Node.NodeKey = Bolt11TestInvoices.NewKey();
        var replaced = await fixture.Service.CheckAsync(fixture.MerchantId, default);
        fixture.Node.NodeKey = pinned;
        fixture.Node.Override = _ => FakePhoenixd.Text(System.Net.HttpStatusCode.BadGateway, "tunnel down");
        var down = await fixture.Service.CheckAsync(fixture.MerchantId, default);

        Assert.True(healthy!.Reachable);
        Assert.Null(healthy.CheckError);
        Assert.Equal(Now.AddHours(1), healthy.CheckedAtUtc);
        Assert.False(replaced!.Reachable);
        Assert.Contains("not the pinned", replaced.CheckError);
        Assert.Equal(configured.NodeId, replaced.NodeId); // the pin is never moved by a check
        Assert.False(down!.Reachable);
        Assert.Equal("The node refused getinfo: HTTP 502 tunnel down", down.CheckError);
        Assert.Equal(configured.NodeId, fixture.Backends().Single().NodeId);
    }

    [Fact]
    public async Task RemovingABackendStopsNewInvoicesButKeepsTheRecord()
    {
        using var fixture = new Fixture();
        await fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(NodeUrl, "limited"), default);

        Assert.True(await fixture.Service.RemoveAsync(fixture.MerchantId, default));
        Assert.False(await fixture.Service.RemoveAsync(fixture.MerchantId, default));
        Assert.Null(await fixture.Service.GetAsync(fixture.MerchantId, default));
        Assert.Null(await fixture.Service.CheckAsync(fixture.MerchantId, default));
        var retired = Assert.Single(fixture.Backends());
        Assert.False(retired.IsActive);
        Assert.NotNull(retired.ProtectedPassword); // still needed to read the invoices it issued
    }

    [Fact]
    public async Task BackendsBelongToOneMerchant()
    {
        using var fixture = new Fixture();
        var other = fixture.Db.AddMerchant("Other").Id;

        await fixture.Service.ConfigureAsync(fixture.MerchantId, Phoenixd(NodeUrl, "limited"), default);
        await fixture.Service.ConfigureAsync(other, new ConfigureLightningBackendRequest { Kind = "operator" }, default);

        Assert.Equal(LightningBackendInfo.PhoenixdKind, (await fixture.Service.GetAsync(fixture.MerchantId, default))!.Kind);
        Assert.Equal(LightningBackendInfo.OperatorKind, (await fixture.Service.GetAsync(other, default))!.Kind);
        Assert.All(fixture.Backends(), b => Assert.True(b.IsActive));
    }

    private static ConfigureLightningBackendRequest Phoenixd(string? url, string password) =>
        new() { Kind = "phoenixd", Url = url, Password = password };

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool fullAccess = false, bool allowInsecureHttp = false, bool credentialKey = true, bool operatorNode = true)
        {
            MerchantId = Db.AddMerchant().Id;
            Node = Network.Add(NodeUrl, new FakePhoenixd("limited", fullAccess));
            Operator = Network.Add("https://operator.node/", new FakePhoenixd("operator-full", fullAccess: true));

            var settings = new LightningBackendOptions { AllowInsecureHttp = allowInsecureHttp };
            if (credentialKey)
                settings.CredentialKeys["k1"] = TestCredentials.NewKey();
            Protector = new AesGcmCredentialProtector(Microsoft.Extensions.Options.Options.Create(settings));

            var factory = new LightningBackendFactory(Network, Protector,
                operatorNode ? new PhoenixConfig { Host = "https://operator.node", Token = "operator-full" } : null);
            Service = new LightningBackendService(Db, factory, Protector, Microsoft.Extensions.Options.Options.Create(settings),
                Clock, NullLogger<LightningBackendService>.Instance);
        }

        public TestDatabase Db { get; } = new();
        public ManualClock Clock { get; } = new(Now);
        public FakeLightningNetwork Network { get; } = new();
        public FakePhoenixd Node { get; }
        public FakePhoenixd Operator { get; }
        public AesGcmCredentialProtector Protector { get; }
        public LightningBackendService Service { get; }
        public int MerchantId { get; }

        public List<LightningBackend> Backends()
        {
            using var db = Db.CreateDbContext();
            return db.LightningBackends.AsNoTracking().OrderBy(b => b.Id).ToList();
        }

        public void Dispose()
        {
            Network.Dispose();
            Db.Dispose();
        }
    }
}
