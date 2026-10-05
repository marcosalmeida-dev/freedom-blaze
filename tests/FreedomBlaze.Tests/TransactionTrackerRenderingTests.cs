using System.Globalization;
using System.Resources;
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
                ConfirmedReceivedSats = 150_000, ConfirmedSpentSats = 50_000,
                PendingReceivedSats = 0, PendingSpentSats = 25_000,
                ConfirmedTransactionCount = 3, PendingTransactionCount = 1,
            },
            Transactions =
            [
                Transaction('a') with { AddressNetSats = -50_000, TrackedReceivedSats = 0, TrackedSpentSats = 50_000, TrackedNetSats = -50_000 },
                Transaction('b') with { AddressNetSats = 10_000, TrackedReceivedSats = 10_000, TrackedSpentSats = 0, TrackedNetSats = 10_000, Confirmed = true, Confirmations = 1 },
                Transaction('c') with { AddressNetSats = 0, TrackedReceivedSats = 0, TrackedSpentSats = 0, TrackedNetSats = 0 },
                Transaction('d') with { AddressNetSats = null, TrackedReceivedSats = null, TrackedSpentSats = null, TrackedNetSats = null },
            ],
        };

        var document = await RenderAsync(result, price: 100_000m);

        var summaries = RequiredNodes(document.DocumentNode, "//section[contains(@class, 'summary-card')]");
        Assert.Contains("75,000 sats", Text(summaries[0]));
        Assert.Contains("150,000 sats", Text(summaries[1]));
        Assert.Contains("75,000 sats", Text(summaries[2]));
        Assert.Contains("100,000 sats", Text(document.DocumentNode));
        Assert.Contains("-25,000 sats", Text(document.DocumentNode));
        Assert.Contains("1 pending", Text(document.DocumentNode));
        Assert.Contains("One address, not an entire wallet", Text(document.DocumentNode));
        var rows = RequiredNodes(document.DocumentNode, "//table/tbody/tr");
        Assert.Equal(4, rows.Count);
        Assert.Equal("-50,000 sats", Text(rows[0].SelectSingleNode("./td[5]//strong")));
        Assert.Equal("+10,000 sats", Text(rows[1].SelectSingleNode("./td[5]//strong")));
        Assert.Equal("0 sats", Text(rows[2].SelectSingleNode("./td[5]//strong")));
        Assert.Null(rows[3].SelectSingleNode("./td[5]//strong"));
        Assert.Equal("—", Text(rows[3].SelectSingleNode("./td[5]")));
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

    [Fact]
    public async Task WalletTotalsUseCompleteSummaryRatherThanOnlyTheVisibleHistory()
    {
        var result = WalletResult() with
        {
            Wallet = new()
            {
                AddressCount = 2, ConfirmedBalanceSats = 250_000, PendingBalanceChangeSats = -25_000,
                ConfirmedReceivedSats = 400_000, ConfirmedSpentSats = 150_000,
                PendingReceivedSats = 20_000, PendingSpentSats = 45_000,
            },
            Transactions = [Transaction() with { TrackedReceivedSats = 10_000, TrackedSpentSats = 0, TrackedNetSats = 10_000 }],
        };

        var document = await RenderAsync(result, price: 100_000m);

        var summaries = RequiredNodes(document.DocumentNode, "//section[contains(@class, 'summary-card')]");
        Assert.Contains("225,000 sats", Text(summaries[0]));
        Assert.Contains("0.00225000 BTC", Text(summaries[0]));
        Assert.Contains("$225.00", Text(summaries[0]));
        Assert.Contains("420,000 sats", Text(summaries[1]));
        Assert.Contains("195,000 sats", Text(summaries[2]));
        Assert.Contains("250,000 sats", Text(document.DocumentNode));
        Assert.Contains("-25,000 sats", Text(document.DocumentNode));
        Assert.DoesNotContain("10,000 sats", Text(summaries[0]));
    }

    [Fact]
    public async Task HistoryDirectionUsesNetFlowSoChangeIsNotCountedAsExternalIncoming()
    {
        var changedPayment = Transaction('a') with
        {
            TrackedReceivedSats = 40_000, TrackedSpentSats = 100_000, TrackedNetSats = -60_000,
        };
        var incoming = Transaction('b') with { TrackedReceivedSats = 25_000, TrackedSpentSats = 0, TrackedNetSats = 25_000 };
        var internalTransfer = Transaction('c') with
        {
            TrackedReceivedSats = 100_000, TrackedSpentSats = 100_000, TrackedNetSats = 0,
        };
        var missingPrevout = Transaction('d') with
        {
            TrackedReceivedSats = 25_000, TrackedSpentSats = null, TrackedNetSats = null,
        };
        var document = await RenderAsync(WalletResult() with
        {
            Transactions = [changedPayment, incoming, internalTransfer, missingPrevout],
        });

        var rows = RequiredNodes(document.DocumentNode, "//table/tbody/tr");
        Assert.Equal("0 sats", Text(rows[0].SelectSingleNode("./td[3]//strong")));
        Assert.Equal("-60,000 sats", Text(rows[0].SelectSingleNode("./td[4]//strong")));
        Assert.Equal("-60,000 sats", Text(rows[0].SelectSingleNode("./td[5]//strong")));
        Assert.Contains("outgoing-value", RequiredNode(rows[0], "./td[5]").GetAttributeValue("class", ""));
        Assert.Equal("+25,000 sats", Text(rows[1].SelectSingleNode("./td[3]//strong")));
        Assert.Equal("0 sats", Text(rows[1].SelectSingleNode("./td[4]//strong")));
        Assert.Contains("incoming-value", RequiredNode(rows[1], "./td[5]").GetAttributeValue("class", ""));
        Assert.Equal("0 sats", Text(rows[2].SelectSingleNode("./td[5]//strong")));
        Assert.Contains("neutral-value", RequiredNode(rows[2], "./td[5]").GetAttributeValue("class", ""));
        Assert.Equal("—", Text(rows[3].SelectSingleNode("./td[5]")));
        Assert.Null(rows[3].SelectSingleNode("./td[5]//strong"));
    }

    [Fact]
    public async Task ConfirmedHistoryUsesSelectedCurrencyDailyPriceRatherThanCurrentRate()
    {
        var date = new DateOnly(2026, 10, 2);
        var transaction = ConfirmedTransaction() with
        {
            TrackedReceivedSats = 100_000, TrackedSpentSats = 0, TrackedNetSats = 100_000,
        };
        var quote = HistoricalPrice(date, "BRL", 250_000m) with { FxDate = date.AddDays(-1) };
        var real = CurrencyModel.CurrencyListStatic.Single(currency => currency.Value == "BRL");

        var document = await RenderAsync(WalletResult() with { Transactions = [transaction] }, price: 500_000m,
            currency: real, culture: "pt-BR", historicalPrices: new Dictionary<DateOnly, BitcoinHistoricalPriceResult> { [date] = quote });

        var row = RequiredNode(document.DocumentNode, "//table/tbody/tr");
        Assert.Contains("R$", Text(row.SelectSingleNode("./td[3]")));
        Assert.Contains("250,00", Text(row.SelectSingleNode("./td[3]")));
        Assert.DoesNotContain("500,00", Text(row.SelectSingleNode("./td[3]")));
        Assert.Contains("250.000,00", Text(row.SelectSingleNode("./td[6]")));
        Assert.Contains("2026-10-02", Text(row.SelectSingleNode("./td[6]")));
        Assert.Contains("2026-10-01", Text(row.SelectSingleNode("./td[6]")));
    }

    [Fact]
    public async Task HistoricalLookupUsesUtcBlockDateAcrossAnOffsetBoundary()
    {
        var utcDate = new DateOnly(2026, 10, 3);
        var transaction = ConfirmedTransaction() with
        {
            BlockTime = new DateTimeOffset(2026, 10, 2, 23, 30, 0, TimeSpan.FromHours(-3)),
            TrackedReceivedSats = 100_000, TrackedSpentSats = 0, TrackedNetSats = 100_000,
        };

        var document = await RenderAsync(WalletResult() with { Transactions = [transaction] }, price: 100_000m,
            historicalPrices: new Dictionary<DateOnly, BitcoinHistoricalPriceResult> { [utcDate] = HistoricalPrice(utcDate, "USD", 80_000m) });

        var priceCell = RequiredNode(document.DocumentNode, "//table/tbody/tr/td[6]");
        Assert.Contains("$80,000.00", Text(priceCell));
        Assert.Contains("2026-10-03", Text(priceCell));
        Assert.DoesNotContain("$100,000.00", Text(priceCell));
    }

    [Theory]
    [InlineData(BitcoinHistoricalPriceStatus.DayNotComplete)]
    [InlineData(BitcoinHistoricalPriceStatus.NoData)]
    [InlineData(BitcoinHistoricalPriceStatus.RateLimited)]
    [InlineData(BitcoinHistoricalPriceStatus.Unavailable)]
    [InlineData(BitcoinHistoricalPriceStatus.UnsupportedCurrency)]
    public async Task UnavailableHistoricalPricesNeverReuseTodaysRateOrBecomeZero(BitcoinHistoricalPriceStatus status)
    {
        var date = new DateOnly(2026, 10, 2);
        var transaction = ConfirmedTransaction() with
        {
            TrackedReceivedSats = 100_000, TrackedSpentSats = 0, TrackedNetSats = 100_000,
        };

        var document = await RenderAsync(WalletResult() with { Transactions = [transaction] }, price: 100_000m,
            historicalPrices: new Dictionary<DateOnly, BitcoinHistoricalPriceResult>
            {
                [date] = HistoricalPrice(date, "USD", 80_000m) with { Status = status, PricePerBitcoin = null },
            });

        var row = RequiredNode(document.DocumentNode, "//table/tbody/tr");
        Assert.DoesNotContain("$100.00", Text(row.SelectSingleNode("./td[3]")));
        Assert.DoesNotContain("$0.00", Text(row.SelectSingleNode("./td[3]")));
        Assert.DoesNotContain("$100,000.00", Text(row.SelectSingleNode("./td[6]")));
        Assert.DoesNotContain("$80,000.00", Text(row.SelectSingleNode("./td[6]")));
        Assert.DoesNotContain("$0.00", Text(row.SelectSingleNode("./td[6]")));
    }

    [Fact]
    public async Task CurrencyChangeCannotFormatAStaleUsdHistoricalQuoteAsBrazilianReais()
    {
        var date = new DateOnly(2026, 10, 2);
        var real = CurrencyModel.CurrencyListStatic.Single(currency => currency.Value == "BRL");
        var transaction = ConfirmedTransaction() with
        {
            TrackedReceivedSats = 100_000, TrackedSpentSats = 0, TrackedNetSats = 100_000,
        };

        var document = await RenderAsync(WalletResult() with { Transactions = [transaction] }, price: 500_000m,
            currency: real, culture: "pt-BR", historicalPrices: new Dictionary<DateOnly, BitcoinHistoricalPriceResult>
            {
                [date] = HistoricalPrice(date, "USD", 80_000m),
            });

        var priceCell = RequiredNode(document.DocumentNode, "//table/tbody/tr/td[6]");
        Assert.DoesNotContain("80.000,00", Text(priceCell));
        Assert.DoesNotContain("500.000,00", Text(priceCell));
    }

    [Fact]
    public async Task PendingHistoryCannotUseAHistoricalQuoteWithoutAConfirmedBlockDate()
    {
        var date = new DateOnly(2026, 10, 2);
        var pending = Transaction() with
        {
            BlockTime = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
            TrackedReceivedSats = 100_000, TrackedSpentSats = 0, TrackedNetSats = 100_000,
        };

        var document = await RenderAsync(WalletResult() with { Transactions = [pending] }, price: 100_000m,
            historicalPrices: new Dictionary<DateOnly, BitcoinHistoricalPriceResult> { [date] = HistoricalPrice(date, "USD", 80_000m) });

        var priceCell = RequiredNode(document.DocumentNode, "//table/tbody/tr/td[6]");
        Assert.DoesNotContain("$80,000.00", Text(priceCell));
        Assert.DoesNotContain("$100,000.00", Text(priceCell));
    }

    [Fact]
    public async Task TransactionTotalsSumAllInputsAndOutputsAndUseTheRequestedFlowColors()
    {
        var transaction = Transaction() with
        {
            Inputs = [new() { Address = "input-1", ValueSats = 100_000 }, new() { Address = "input-2", ValueSats = 50_000 }],
            Outputs = [new() { Address = Address, ValueSats = 120_000 }, new() { Address = "change-address", ValueSats = 29_750 }],
            FeeSats = 250,
        };

        var document = await RenderAsync(TransactionResult(transaction), price: 100_000m);

        var totals = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'transaction-totals')]/section");
        Assert.Contains("150,000 sats", Text(totals[0]));
        Assert.Contains("149,750 sats", Text(totals[1]));
        Assert.NotNull(totals[0].SelectSingleNode(".//*[contains(@class, 'incoming-value')]"));
        Assert.NotNull(totals[1].SelectSingleNode(".//*[contains(@class, 'outgoing-value')]"));
        var lists = RequiredNodes(document.DocumentNode, "//ol[contains(@class, 'io-list')]");
        Assert.All(RequiredNodes(lists[0], "./li/strong"), node => Assert.Contains("incoming-value", node.GetAttributeValue("class", "")));
        Assert.All(RequiredNodes(lists[1], "./li/strong"), node => Assert.Contains("outgoing-value", node.GetAttributeValue("class", "")));
    }

    [Fact]
    public async Task MissingInputValuesKeepTheTotalUnknownWhileZeroOutputsRemainKnownZero()
    {
        var transaction = Transaction() with
        {
            Inputs = [new() { IsCoinbase = true }], Outputs = [new() { ValueSats = 0 }], FeeSats = 0,
        };

        var document = await RenderAsync(TransactionResult(transaction));

        var totals = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'transaction-totals')]/section");
        Assert.Contains("—", Text(totals[0]));
        Assert.DoesNotContain("0 sats", Text(totals[0]));
        Assert.Contains("0 sats", Text(totals[1]));
    }

    [Fact]
    public async Task TransactionDetailsPreserveWatchOnlyContextAndSeparateHistoricalFromCurrentAmounts()
    {
        var date = new DateOnly(2026, 10, 2);
        var transaction = BitcoinWalletAccounting.ApplyContext(ConfirmedTransaction(), [Address]);
        var result = TransactionResult(transaction) with { TrackedAddresses = [Address] };

        var document = await RenderAsync(result, price: 100_000m,
            historicalPrices: new Dictionary<DateOnly, BitcoinHistoricalPriceResult> { [date] = HistoricalPrice(date, "USD", 80_000m) });

        var walletNet = RequiredNode(document.DocumentNode, "//p[contains(@class, 'wallet-transaction-net')]");
        Assert.Contains("+100,000 sats", Text(walletNet));
        Assert.Contains("$80.00", Text(walletNet));
        Assert.Contains("incoming-value", walletNet.GetAttributeValue("class", ""));
        Assert.Contains(Address, Text(RequiredNode(document.DocumentNode, "//details[contains(@class, 'tracked-context')]")));
        var lists = RequiredNodes(document.DocumentNode, "//ol[contains(@class, 'io-list')]");
        Assert.Contains("$100.50", Text(lists[0]));
        Assert.Contains("$80.40", Text(RequiredNode(lists[0], ".//*[contains(@class, 'historical-value')]")));
        Assert.Contains("$100.00", Text(lists[1]));
        Assert.Contains("$80.00", Text(RequiredNode(lists[1], ".//*[contains(@class, 'historical-value')]")));
        var quoteCard = RequiredNode(document.DocumentNode, "//div[contains(@class, 'transaction-totals')]/section[3]");
        Assert.Contains("$80,000.00", Text(quoteCard));
        Assert.Contains("2026-10-02", Text(quoteCard));
    }

    [Fact]
    public async Task WalletAveragePricesUseGrossSatoshiWeightsAndExcludePendingAmounts()
    {
        var result = AverageWalletResult();

        var document = await RenderAsync(result, price: 500_000m, historicalPrices: AveragePrices());

        var cards = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'summary-grid')][1]/section");
        Assert.Contains("290,000 sats", Text(cards[1]));
        Assert.Contains("380,000 sats", Text(cards[2]));
        Assert.Contains("$70,000.00", Text(cards[1]));
        Assert.Contains("$90,000.00", Text(cards[2]));
        Assert.DoesNotContain("$500,000.00", Text(cards[1]));
        Assert.DoesNotContain("$500,000.00", Text(cards[2]));
        Assert.Equal("true", RequiredNode(cards[1], ".//div[@data-direction='received']").GetAttributeValue("data-complete", ""));
        Assert.Equal("true", RequiredNode(cards[2], ".//div[@data-direction='spent']").GetAttributeValue("data-complete", ""));
    }

    [Fact]
    public async Task WalletAveragePricesUseTheSelectedHistoricalCurrency()
    {
        var real = CurrencyModel.CurrencyListStatic.Single(currency => currency.Value == "BRL");
        var quotes = AveragePrices("BRL", 200_000m, 300_000m);

        var document = await RenderAsync(AverageWalletResult(), price: 600_000m, currency: real,
            culture: "pt-BR", historicalPrices: quotes);

        var cards = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'summary-grid')][1]/section");
        Assert.Contains("R$", Text(cards[1]));
        Assert.Contains("225.000,00", Text(cards[1]));
        Assert.Contains("275.000,00", Text(cards[2]));
        Assert.DoesNotContain("600.000,00", Text(cards[1]));
        Assert.DoesNotContain("70.000,00", Text(cards[1]));
    }

    [Fact]
    public async Task UnavailableWalletAveragesDoNotBecomeZeroOrUseCurrentPrices()
    {
        var prices = AveragePrices().ToDictionary(pair => pair.Key, pair => pair.Value with
        {
            Status = BitcoinHistoricalPriceStatus.Unavailable, PricePerBitcoin = null,
        });

        var document = await RenderAsync(AverageWalletResult(), price: 500_000m, historicalPrices: prices);

        var cards = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'summary-grid')][1]/section");
        Assert.DoesNotContain("$70,000.00", Text(cards[1]));
        Assert.DoesNotContain("$90,000.00", Text(cards[2]));
        Assert.DoesNotContain("$500,000.00", Text(cards[1]));
        Assert.DoesNotContain("$0.00", Text(cards[1]));
        Assert.DoesNotContain("$0.00", Text(cards[2]));
    }

    [Fact]
    public async Task AStaleCurrencyCannotAppearAsASelectedCurrencyWalletAverage()
    {
        var real = CurrencyModel.CurrencyListStatic.Single(currency => currency.Value == "BRL");

        var document = await RenderAsync(AverageWalletResult(), price: 600_000m, currency: real,
            culture: "pt-BR", historicalPrices: AveragePrices());

        var cards = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'summary-grid')][1]/section");
        Assert.DoesNotContain("70.000,00", Text(cards[1]));
        Assert.DoesNotContain("90.000,00", Text(cards[2]));
        Assert.DoesNotContain("600.000,00", Text(cards[1]));
    }

    [Fact]
    public async Task PartialAverageCoverageUsesAllConfirmedAmountsRatherThanOnlyThePricedRows()
    {
        var quotes = AveragePrices().Where(pair => pair.Key == new DateOnly(2024, 1, 6))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var document = await RenderAsync(AverageWalletResult(), historicalPrices: quotes);

        var received = RequiredNode(document.DocumentNode, "//div[@data-direction='received']");
        var spent = RequiredNode(document.DocumentNode, "//div[@data-direction='spent']");
        Assert.Equal("false", received.GetAttributeValue("data-complete", ""));
        Assert.Equal("false", spent.GetAttributeValue("data-complete", ""));
        Assert.Contains("$60,000.00", Text(RequiredNode(received, ".//p[contains(@class, 'average-value')]")));
        Assert.Contains("$60,000.00", Text(RequiredNode(spent, ".//p[contains(@class, 'average-value')]")));
        var receivedCoverage = Text(RequiredNode(received, ".//small[contains(@class, 'average-coverage')]"));
        var spentCoverage = Text(RequiredNode(spent, ".//small[contains(@class, 'average-coverage')]"));
        Assert.Contains("0.00150000", receivedCoverage);
        Assert.Contains("0.00200000", receivedCoverage);
        Assert.Contains("0.00050000", spentCoverage);
        Assert.Contains("0.00200000", spentCoverage);
    }

    [Fact]
    public async Task UnknownSpentAmountsDoNotEraseKnownReceivedCoverageOrInventCompleteSpendingCoverage()
    {
        var result = AverageWalletResult();
        result = result with
        {
            Transactions = result.Transactions.Select(transaction => transaction.TxId == new string('b', 64)
                ? transaction with { TrackedSpentSats = null, TrackedNetSats = null }
                : transaction).ToArray(),
        };

        var document = await RenderAsync(result, historicalPrices: AveragePrices());

        var received = RequiredNode(document.DocumentNode, "//div[@data-direction='received']");
        var spent = RequiredNode(document.DocumentNode, "//div[@data-direction='spent']");
        Assert.Equal("true", received.GetAttributeValue("data-complete", ""));
        Assert.Equal("false", spent.GetAttributeValue("data-complete", ""));
        Assert.Contains("$70,000.00", Text(RequiredNode(received, ".//p[contains(@class, 'average-value')]")));
        Assert.Contains("$60,000.00", Text(RequiredNode(spent, ".//p[contains(@class, 'average-value')]")));
        Assert.Contains("0.00050000", Text(RequiredNode(spent, ".//small[contains(@class, 'average-coverage')]")));
        Assert.Contains("0.00200000", Text(RequiredNode(spent, ".//small[contains(@class, 'average-coverage')]")));
    }

    [Fact]
    public async Task AWalletWithoutConfirmedFlowsHasUnavailableAveragesInsteadOfZeroBitcoinPrices()
    {
        var document = await RenderAsync(WalletResult(), price: 100_000m);

        var averages = RequiredNodes(document.DocumentNode, "//div[contains(@class, 'average-price')]");
        Assert.Equal(2, averages.Count);
        Assert.All(averages, average =>
        {
            Assert.Equal("false", average.GetAttributeValue("data-complete", ""));
            var value = Text(RequiredNode(average, ".//p[contains(@class, 'average-value')]"));
            Assert.Contains("—", value);
            Assert.DoesNotContain("$0.00", value);
            Assert.DoesNotContain("$100,000.00", value);
        });
    }

    [Fact]
    public async Task InconsistentHistoryIsExplainedWithoutDisplayingAFabricatedAverage()
    {
        var result = AverageWalletResult();
        result = result with { Wallet = result.Wallet! with { ConfirmedReceivedSats = 100_000 } };

        var document = await RenderAsync(result, historicalPrices: AveragePrices());

        var received = RequiredNode(document.DocumentNode, "//div[@data-direction='received']");
        Assert.Equal("false", received.GetAttributeValue("data-complete", ""));
        Assert.Contains("—", Text(RequiredNode(received, ".//p[contains(@class, 'average-value')]")));
        Assert.DoesNotContain("$70,000.00", Text(received));
        Assert.Contains("History totals do not match", Text(RequiredNode(received, ".//small[contains(@class, 'average-coverage')]")));
    }

    private static BitcoinTrackingResult AverageWalletResult() => WalletResult() with
    {
        Wallet = new()
        {
            AddressCount = 2, ConfirmedReceivedSats = 200_000, ConfirmedSpentSats = 200_000,
            PendingReceivedSats = 90_000, PendingSpentSats = 180_000, PendingBalanceChangeSats = -90_000,
        },
        Transactions =
        [
            ConfirmedTransaction() with
            {
                BlockTime = new DateTimeOffset(2024, 1, 6, 12, 0, 0, TimeSpan.Zero),
                TrackedReceivedSats = 150_000, TrackedSpentSats = 50_000, TrackedNetSats = 100_000,
            },
            ConfirmedTransaction() with
            {
                TxId = new string('b', 64), BlockTime = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.Zero),
                TrackedReceivedSats = 50_000, TrackedSpentSats = 150_000, TrackedNetSats = -100_000,
            },
            Transaction('c') with
            {
                BlockTime = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.Zero),
                TrackedReceivedSats = 90_000, TrackedSpentSats = 180_000, TrackedNetSats = -90_000,
            },
        ],
    };

    private static IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult> AveragePrices(
        string currency = "USD", decimal firstPrice = 60_000m, decimal secondPrice = 100_000m) =>
        new Dictionary<DateOnly, BitcoinHistoricalPriceResult>
        {
            [new(2024, 1, 6)] = HistoricalPrice(new(2024, 1, 6), currency, firstPrice),
            [new(2024, 1, 7)] = HistoricalPrice(new(2024, 1, 7), currency, secondPrice),
        };

    private static BitcoinTransaction ConfirmedTransaction() => Transaction() with
    {
        Confirmed = true, Confirmations = 6, BlockHeight = 970_123,
        BlockHash = new string('b', 64), BlockTime = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
    };

    private static BitcoinHistoricalPriceResult HistoricalPrice(DateOnly date, string currency, decimal price) => new()
    {
        Date = date, Currency = currency, Status = BitcoinHistoricalPriceStatus.Available,
        PricePerBitcoin = price, BitcoinUsdClose = currency == "USD" ? price : 80_000m,
        UsdToCurrencyRate = currency == "USD" ? 1m : price / 80_000m,
        FxDate = currency == "USD" ? null : date,
        RetrievedAt = new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero),
    };

    private static BitcoinTrackingResult WalletResult() => new()
    {
        Query = $"{Address},1BoatSLRHtKNngkdXEeobR76b53LETtpyT", Kind = BitcoinTrackingKind.Wallet,
        Wallet = new() { AddressCount = 2 }, TrackedAddresses = [Address, "1BoatSLRHtKNngkdXEeobR76b53LETtpyT"],
        TipHeight = 970_128, UpdatedAt = new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero),
    };

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
        bool loadingMore = false, Currency? currency = null, string culture = "en-US",
        IReadOnlyDictionary<DateOnly, BitcoinHistoricalPriceResult>? historicalPrices = null, bool loadingPrices = false)
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
                    [nameof(BitcoinTrackingDetails.HistoricalPrices)] = historicalPrices ?? new Dictionary<DateOnly, BitcoinHistoricalPriceResult>(),
                    [nameof(BitcoinTrackingDetails.LoadingPrices)] = loadingPrices,
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
        private static readonly ResourceManager FallbackLabels = new("FreedomBlaze.Resources.Localization", typeof(Localization).Assembly);
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
            ["Tracker.AverageCoverage"] = "Quoted {0} BTC / {1} BTC confirmed",
            ["Tracker.AverageInconsistent"] = "History totals do not match the confirmed summary; refresh to recalculate",
        };

        public LocalizedString this[string name] => new(name, Labels.GetValueOrDefault(name)
            ?? FallbackLabels.GetString(name, CultureInfo.GetCultureInfo("en-US")) ?? name);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.CurrentCulture, this[name].Value, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Labels.Select(pair => new LocalizedString(pair.Key, pair.Value));
    }
}
