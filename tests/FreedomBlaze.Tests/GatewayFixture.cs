using FreedomBlaze.Data.Entities;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using FreedomBlaze.Services.Lightning;
using FreedomBlaze.Services.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreedomBlaze.Tests;

/// <summary>
/// The whole gateway over fakes: one merchant on its own fake phoenixd, the real invoice service, background
/// sweep, webhook outbox, dispatcher and endpoint service on SQLite, and a fake receiver for the webhooks.
/// The clock stands still until a test moves it. R$ 40 prices at exactly 10,000 sats.
/// </summary>
internal sealed class GatewayFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    public const string NodeUrl = "https://merchant.node/";
    public const string EndpointUrl = "https://shop.example/webhooks/lightning";

    public GatewayFixture(LightningWebhookOptions? webhookOptions = null, LightningInvoiceOptions? invoiceOptions = null,
        bool credentialKey = true)
    {
        Protector = credentialKey
            ? TestCredentials.Protector()
            : new AesGcmCredentialProtector(Microsoft.Extensions.Options.Options.Create(new LightningBackendOptions()));
        var nodePasswords = TestCredentials.Protector();

        Node = Network.Add(NodeUrl, new FakePhoenixd("limited"));
        MerchantId = Db.AddMerchant().Id;
        Backend = Db.Add(new LightningBackend
        {
            MerchantId = MerchantId, Kind = LightningBackendKind.Phoenixd, Url = NodeUrl,
            ProtectedPassword = nodePasswords.Protect(MerchantId, "limited"),
            IsActive = true, CreatedAtUtc = Start, NodeId = Node.NodeId, Chain = "mainnet",
        });

        var factory = new LightningBackendFactory(Network, nodePasswords);
        var workerOptions = Microsoft.Extensions.Options.Options.Create(new LightningWorkerOptions());
        Refresher = new LightningInvoiceRefresher(Db, factory, new WebhookOutbox(Clock), Clock,
            NullLogger<LightningInvoiceRefresher>.Instance);
        var refresher = Refresher;

        Sweeper = new LightningInvoiceSweeper(Db, refresher, workerOptions, Clock, NullLogger<LightningInvoiceSweeper>.Instance);
        Dispatcher = new WebhookDispatcher(Db, Receiver, Protector, workerOptions, Clock, NullLogger<WebhookDispatcher>.Instance);
        Endpoints = new WebhookEndpointService(Db, Protector, Dispatcher,
            Microsoft.Extensions.Options.Options.Create(webhookOptions ?? new LightningWebhookOptions()),
            Microsoft.Extensions.Options.Options.Create(invoiceOptions ?? new LightningInvoiceOptions()),
            Clock, NullLogger<WebhookEndpointService>.Instance);
        Invoices = new LightningInvoiceService(
            new CurrencyConversionService(new StubRates(), Clock), Db.Repository<LightningInvoice>(), Db.Repository<LightningBackend>(),
            factory, refresher, Microsoft.Extensions.Options.Options.Create(new LightningInvoiceOptions()),
            Clock, NullLogger<LightningInvoiceService>.Instance);
        Worker = new LightningGatewayWorker(Sweeper, Dispatcher, workerOptions, Clock, new StartedLifetime(),
            NullLogger<LightningGatewayWorker>.Instance);
    }

    public TestDatabase Db { get; } = new();
    public ManualClock Clock { get; } = new(Start);
    public FakeLightningNetwork Network { get; } = new();
    public FakeWebhookReceiver Receiver { get; } = new();
    public FakePhoenixd Node { get; }
    public AesGcmCredentialProtector Protector { get; }
    public int MerchantId { get; }
    public LightningBackend Backend { get; }
    public LightningInvoiceService Invoices { get; }
    public LightningInvoiceRefresher Refresher { get; }
    public LightningInvoiceSweeper Sweeper { get; }
    public WebhookDispatcher Dispatcher { get; }
    public WebhookEndpointService Endpoints { get; }
    public LightningGatewayWorker Worker { get; }

    public Task<WebhookEndpointWithSecret> AddEndpointAsync(string url = EndpointUrl, int? merchantId = null) =>
        Endpoints.CreateAsync(merchantId ?? MerchantId, new CreateWebhookEndpointRequest { Url = url }, default);

    /// <summary>Issues an R$ 40 invoice (10,000 sats, ten minutes) and returns it.</summary>
    public async Task<LightningInvoice> IssueAsync(string? externalId = "order-123")
    {
        var result = await Invoices.CreateAsync(new InvoiceIssuer(MerchantId, null),
            new CreateLightningInvoiceRequest { Amount = 40m, Currency = "BRL", ExternalId = externalId }, null, default);
        return result.Invoice ?? throw new InvalidOperationException($"Not issued: {result.Outcome} {result.Message}");
    }

    public LightningInvoice Stored(long invoiceId) => Db.Invoices().Single(i => i.Id == invoiceId);

    public void Dispose()
    {
        Network.Dispose();
        Receiver.Dispose();
        Db.Dispose();
    }

    private sealed class StubRates : IExchangeRateService
    {
        public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; } = [];

        public Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BitcoinExchangeRateModel?>(new BitcoinExchangeRateModel
            {
                BitcoinRateInUSD = 80_000m,
                SourceCount = 3,
                CurrencyExchangeRate = new CurrencyExchangeRateModel
                {
                    Date = Start.AddHours(-2).UtcDateTime,
                    Rates = [new() { Currency = "USD", Rate = 1m }, new() { Currency = "BRL", Rate = 5m }]
                }
            });
    }

    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
