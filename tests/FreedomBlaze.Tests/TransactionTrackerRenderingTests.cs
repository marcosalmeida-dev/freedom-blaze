using System.Globalization;
using FreedomBlaze.Components.Pages;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Resources;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace FreedomBlaze.Tests;

public sealed class TransactionTrackerRenderingTests
{
    private const string Address = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
    private static readonly string TransactionId = new('a', 64);

    [Fact]
    public async Task PendingTransactionShowsZeroConfirmationsAndUsesWeightForItsFeeRate()
    {
        var transaction = Transaction() with { FeeSats = 502, Weight = 1_001, SizeBytes = 900 };

        var document = await RenderAsync(TransactionResult(transaction), price: 100_000m);

        Assert.Equal("Pending", Text(document.DocumentNode.SelectSingleNode("//span[contains(@class, 'status-badge')]")));
        var summaries = RequiredNodes(document.DocumentNode, "//section[contains(@class, 'summary-card')]");
        Assert.Contains("0 confirmations", Text(summaries[0]));
        Assert.Contains("502 sats", Text(summaries[1]));
        Assert.Contains("2.00 sat/vB", Text(summaries[2]));
        Assert.Contains("251 virtual bytes", Text(summaries[2]));
        Assert.Contains("Waiting for a block", Text(document.DocumentNode));
        Assert.DoesNotContain("Block height", Text(document.DocumentNode));
    }

    [Fact]
    public async Task ConfirmedTransactionShowsItsSnapshotConfirmationCountAndBlockTime()
    {
        var time = new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.Zero);
        var transaction = Transaction() with
        {
            Confirmed = true, Confirmations = 6, BlockHeight = 970_123,
            BlockHash = new string('b', 64), BlockTime = time,
        };

        var document = await RenderAsync(TransactionResult(transaction));

        Assert.Equal("Confirmed", Text(document.DocumentNode.SelectSingleNode("//span[contains(@class, 'status-badge')]")));
        Assert.Contains("6 confirmations", Text(document.DocumentNode));
        Assert.Contains("Block height 970,123", Text(document.DocumentNode));
        var renderedTime = RequiredNode(document.DocumentNode, "//time");
        Assert.Equal(time.ToString("O"), HtmlEntity.DeEntitize(renderedTime.GetAttributeValue("datetime", "")));
        Assert.Contains("UTC", Text(renderedTime));
        Assert.DoesNotContain("Waiting for a block", Text(document.DocumentNode));
    }

    [Fact]
    public async Task MultipleOutputsRemainSeparateFromInputsAndChange()
    {
        var transaction = Transaction() with
        {
            FeeSats = 1_000,
            Inputs = [new() { Address = "input-address", ValueSats = 200_001_000 }],
            Outputs =
            [
                new() { Address = "recipient-address", ValueSats = 120_000_000 },
                new() { Address = "change-address", ValueSats = 80_000_000 },
            ],
        };

        var document = await RenderAsync(TransactionResult(transaction), price: 100_000m);

        var lists = RequiredNodes(document.DocumentNode, "//ol[contains(@class, 'io-list')]");
        Assert.Contains("200,001,000 sats", Text(lists[0]));
        var outputs = RequiredNodes(lists[1], "./li");
        Assert.Equal(2, outputs.Count);
        Assert.Contains("recipient-address", Text(outputs[0]));
        Assert.Contains("120,000,000 sats", Text(outputs[0]));
        Assert.Contains("1.20000000 BTC", Text(outputs[0]));
        Assert.Contains("$120,000.00", Text(outputs[0]));
        Assert.Contains("change-address", Text(outputs[1]));
        Assert.Contains("80,000,000 sats", Text(outputs[1]));
        Assert.Equal("0", lists[1].GetAttributeValue("start", null));
        Assert.Contains("Outputs may include change", Text(document.DocumentNode));
    }

    [Fact]
    public async Task CoinbaseAndZeroValueScriptOutputRenderWithoutInventingAnInputValue()
    {
        var transaction = Transaction() with
        {
            FeeSats = 0,
            Inputs = [new() { IsCoinbase = true }],
            Outputs = [new() { ValueSats = 0 }],
        };

        var document = await RenderAsync(TransactionResult(transaction));

        var lists = RequiredNodes(document.DocumentNode, "//ol[contains(@class, 'io-list')]");
        Assert.Contains("Coinbase", Text(lists[0]));
        Assert.Null(lists[0].SelectSingleNode(".//strong"));
        Assert.Contains("Script output", Text(lists[1]));
        Assert.Contains("0 sats", Text(lists[1]));
        Assert.Contains("0.00000000 BTC", Text(lists[1]));
        Assert.Contains("$0.00", Text(lists[1]));
        Assert.Contains("0.00 sat/vB", Text(document.DocumentNode));
    }

    [Fact]
    public async Task AddressBalanceAndHistoryPreserveNegativePositiveZeroAndUnknownNetChanges()
    {
        var result = AddressResult() with
        {
            Address = new()
            {
                ConfirmedBalanceSats = 100_000, PendingBalanceChangeSats = -25_000,
                ConfirmedTransactionCount = 3, PendingTransactionCount = 1,
            },
            Transactions =
            [
                Transaction('a') with { AddressNetSats = -50_000 },
                Transaction('b') with { AddressNetSats = 10_000, Confirmed = true, Confirmations = 1 },
                Transaction('c') with { AddressNetSats = 0 },
                Transaction('d') with { AddressNetSats = null },
            ],
        };

        var document = await RenderAsync(result, price: 100_000m);

        var summaries = RequiredNodes(document.DocumentNode, "//section[contains(@class, 'summary-card')]");
        Assert.Contains("100,000 sats", Text(summaries[0]));
        Assert.Contains("-25,000 sats", Text(summaries[1]));
        Assert.Contains("1 pending", Text(summaries[2]));
        Assert.Contains("One address, not an entire wallet", Text(document.DocumentNode));
        var rows = RequiredNodes(document.DocumentNode, "//table/tbody/tr");
        Assert.Equal(4, rows.Count);
        Assert.Equal("-50,000 sats", Text(rows[0].SelectSingleNode(".//strong")));
        Assert.Equal("+10,000 sats", Text(rows[1].SelectSingleNode(".//strong")));
        Assert.Equal("0 sats", Text(rows[2].SelectSingleNode(".//strong")));
        Assert.Null(rows[3].SelectSingleNode(".//strong"));
        Assert.Equal("—", Text(rows[3].SelectSingleNode("./td[last()]")));
        Assert.Equal("Pending", Text(rows[0].SelectSingleNode(".//span[contains(@class, 'status-badge')]")));
        Assert.Equal("1 confirmations", Text(rows[1].SelectSingleNode(".//span[contains(@class, 'status-badge')]")));
    }

    [Theory]
    [InlineData(false, "Load older transactions")]
    [InlineData(true, "Loading older transactions")]
    public async Task PaginationShowsItsLoadingStateAndDisablesTransactionSelectionDuringFetch(bool loading, string label)
    {
        var result = AddressResult() with { NextCursor = TransactionId, Transactions = [Transaction()] };

        var document = await RenderAsync(result, loadingMore: loading);

        var button = RequiredNode(document.DocumentNode, "//div[contains(@class, 'pagination')]/button");
        Assert.Equal(label, Text(button));
        Assert.Equal(loading, button.Attributes["disabled"] is not null);
        var transactionButton = RequiredNode(document.DocumentNode, "//button[contains(@class, 'transaction-link')]");
        Assert.Equal(loading, transactionButton.Attributes["disabled"] is not null);
        Assert.Equal(TransactionId, transactionButton.GetAttributeValue("title", null));
        Assert.Contains(TransactionId, transactionButton.GetAttributeValue("aria-label", ""));
    }

    [Fact]
    public async Task UnusedAddressShowsZeroBalanceAndAnEmptyHistoryWithoutPagination()
    {
        var document = await RenderAsync(AddressResult());

        Assert.Contains("0 sats", Text(document.DocumentNode));
        Assert.Contains("No transactions for this address", Text(document.DocumentNode));
        Assert.Null(document.DocumentNode.SelectSingleNode("//table"));
        Assert.Null(document.DocumentNode.SelectSingleNode("//div[contains(@class, 'pagination')]"));
    }

    [Fact]
    public async Task LargeTransactionAndMempoolHistoriesDiscloseTheVisibleLimits()
    {
        var transaction = Transaction() with
        {
            Outputs = Enumerable.Range(1, 30).Select(index => new BitcoinTransactionOutput
            {
                Address = $"output-{index}", ValueSats = index,
            }).ToArray(),
        };

        var transactionDocument = await RenderAsync(TransactionResult(transaction));
        var outputs = RequiredNodes(transactionDocument.DocumentNode, "//ol[@start='0']/li");
        Assert.Equal(25, outputs.Count);
        Assert.Contains("Show more outputs", Text(transactionDocument.DocumentNode));
        Assert.Contains("Outputs (30)", Text(transactionDocument.DocumentNode));

        var addressDocument = await RenderAsync(AddressResult() with { Address = new() { PendingTransactionCount = 51 } });
        Assert.Contains("Only the first 50 pending transactions are listed", Text(addressDocument.DocumentNode));
    }

    [Fact]
    public async Task FiatUsesTheSelectedCurrencyAndUnknownRatesDoNotBecomeZero()
    {
        var result = TransactionResult(Transaction() with
        {
            Outputs = [new() { Address = Address, ValueSats = 100_000_000 }],
        });
        var real = CurrencyModel.CurrencyListStatic.Single(currency => currency.Value == "BRL");

        var document = await RenderAsync(result, price: 500_000m, currency: real, culture: "pt-BR");
        var output = document.DocumentNode.SelectSingleNode("//ol[@start='0']/li");
        Assert.Contains("1,00000000 BTC", Text(output));
        Assert.Contains("500.000,00", Text(output));
        Assert.Contains("R$", Text(output));

        var unknownRate = await RenderAsync(result);
        var unknownOutput = unknownRate.DocumentNode.SelectSingleNode("//ol[@start='0']/li");
        Assert.Contains("—", Text(unknownOutput));
        Assert.DoesNotContain("$0.00", Text(unknownOutput));
    }

    private static BitcoinTransaction Transaction(char id = 'a') => new()
    {
        TxId = new string(id, 64), Weight = 1_000, SizeBytes = 350, FeeSats = 500,
        Inputs = [new() { Address = "input-address", ValueSats = 100_500 }],
        Outputs = [new() { Address = Address, ValueSats = 100_000 }],
    };

    private static BitcoinTrackingResult TransactionResult(BitcoinTransaction transaction) => new()
    {
        Query = transaction.TxId, Kind = BitcoinTrackingKind.Transaction, Transaction = transaction,
        TipHeight = 970_128, UpdatedAt = new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero),
    };

    private static BitcoinTrackingResult AddressResult() => new()
    {
        Query = Address, Kind = BitcoinTrackingKind.Address, Address = new(),
        TipHeight = 970_128, UpdatedAt = new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero),
    };

    private static async Task<HtmlDocument> RenderAsync(BitcoinTrackingResult result, decimal? price = null,
        bool loadingMore = false, Currency? currency = null, string culture = "en-US")
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IStringLocalizer<Localization>>(new TestLocalizer());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<BitcoinTrackingDetails>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(BitcoinTrackingDetails.Result)] = result,
                    [nameof(BitcoinTrackingDetails.SelectedCurrency)] = currency ?? CurrencyModel.CurrencyListStatic[0],
                    [nameof(BitcoinTrackingDetails.FiatPricePerBitcoin)] = price,
                    [nameof(BitcoinTrackingDetails.LoadingMore)] = loadingMore,
                }));
                return component.ToHtmlString();
            });
            var document = new HtmlDocument();
            document.LoadHtml(html);
            return document;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static string Text(HtmlNode? node) => node is null ? string.Empty
        : string.Join(' ', HtmlEntity.DeEntitize(node.InnerText).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HtmlNode RequiredNode(HtmlNode node, string path) => node.SelectSingleNode(path)
        ?? throw new Xunit.Sdk.XunitException($"Expected an element matching {path}.");

    private static HtmlNodeCollection RequiredNodes(HtmlNode node, string path) => node.SelectNodes(path)
        ?? throw new Xunit.Sdk.XunitException($"Expected elements matching {path}.");

    // Keep rendering assertions focused on domain values and semantic states. Resource coverage is
    // tested separately; this localizer makes field labels deterministic in every culture.
    private sealed class TestLocalizer : IStringLocalizer<Localization>
    {
        private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
        {
            ["Tracker.Pending"] = "Pending",
            ["Tracker.Confirmed"] = "Confirmed",
            ["Tracker.ConfirmationsValue"] = "{0} confirmations",
            ["Tracker.VirtualSize"] = "{0} virtual bytes",
            ["Tracker.PendingNote"] = "Waiting for a block",
            ["Tracker.BlockHeight"] = "Block height {0}",
            ["Tracker.OutputsNote"] = "Outputs may include change",
            ["Tracker.CoinbaseInput"] = "Coinbase",
            ["Tracker.Script"] = "Script output",
            ["Tracker.PendingCount"] = "{0} pending",
            ["Tracker.AddressNote"] = "One address, not an entire wallet",
            ["Tracker.OpenTransaction"] = "Open transaction {0}",
            ["Tracker.LoadMore"] = "Load older transactions",
            ["Tracker.LoadingMore"] = "Loading older transactions",
            ["Tracker.NoTransactions"] = "No transactions for this address",
            ["Tracker.Outputs"] = "Outputs",
            ["Tracker.ShowMoreOutputs"] = "Show more outputs",
            ["Tracker.MempoolLimit"] = "Only the first 50 pending transactions are listed",
        };

        public LocalizedString this[string name] => new(name, Labels.GetValueOrDefault(name, name));
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.CurrentCulture, this[name].Value, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Labels.Select(pair => new LocalizedString(pair.Key, pair.Value));
    }
}
