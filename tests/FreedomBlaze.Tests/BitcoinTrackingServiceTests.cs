using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FreedomBlaze.Clients.BitcoinTracking;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Options;
using FreedomBlaze.Services;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Tests;

public sealed class BitcoinTrackingServiceTests
{
    [Fact]
    public async Task PendingTransactionRetainsExactSatsAndDoesNotInventConfirmations()
    {
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("100"),
            _ => TrackingTestData.Json(TrackingTestData.Transaction()),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.TxId.ToUpperInvariant());

        Assert.Equal(BitcoinTrackingKind.Transaction, result.Kind);
        Assert.Equal(TrackingTestData.TxId, result.Query);
        Assert.Null(result.Address);
        Assert.Empty(result.Transactions);
        var transaction = Assert.IsType<BitcoinTransaction>(result.Transaction);
        Assert.False(transaction.Confirmed);
        Assert.Equal(0, transaction.Confirmations);
        Assert.Null(transaction.BlockTime);
        Assert.Null(transaction.BlockHeight);
        Assert.Null(transaction.AddressNetSats);
        Assert.Equal(500L, transaction.FeeSats);
        Assert.Equal(100_000L, transaction.Inputs[0].ValueSats);
        Assert.Equal(9_500L, transaction.Outputs[1].ValueSats);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task ConfirmedTransactionVerifiesBestChainAndCountsIncludingItsBlock()
    {
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            "block-height/100" => TrackingTestData.Text(TrackingTestData.BlockHash),
            _ => TrackingTestData.Json(TrackingTestData.Transaction(confirmed: true)),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.TxId);

        Assert.True(result.Transaction!.Confirmed);
        Assert.Equal(3, result.Transaction.Confirmations);
        Assert.Equal(100, result.Transaction.BlockHeight);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), result.Transaction.BlockTime);
        Assert.Contains("block-height/100", handler.Paths);
    }

    [Fact]
    public async Task AddressSummaryKeepsConfirmedBalanceAndNegativePendingChangeSeparate()
    {
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            var value when value.EndsWith("/txs/mempool", StringComparison.Ordinal) =>
                TrackingTestData.Json(new[] { TrackingTestData.Transaction() }),
            var value when value.EndsWith("/txs/chain", StringComparison.Ordinal) => TrackingTestData.Json(Array.Empty<EsploraTransaction>()),
            _ => TrackingTestData.Json(TrackingTestData.Address()),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.AddressValue);

        Assert.Equal(BitcoinTrackingKind.Address, result.Kind);
        Assert.Null(result.Transaction);
        Assert.Equal(100_000L, result.Address!.ConfirmedBalanceSats);
        Assert.Equal(-90_500L, result.Address.PendingBalanceChangeSats);
        Assert.Equal(-90_500L, Assert.Single(result.Transactions).AddressNetSats);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task EmptyAddressIsAValidZeroBalanceRatherThanNotFound()
    {
        var empty = TrackingTestData.Address() with
        {
            Chain = new EsploraAddressStats { FundedSats = 0, SpentSats = 0, TransactionCount = 0 },
            Mempool = new EsploraAddressStats { FundedSats = 0, SpentSats = 0, TransactionCount = 0 },
        };
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            var value when value.Contains("/txs/", StringComparison.Ordinal) => TrackingTestData.Json(Array.Empty<EsploraTransaction>()),
            _ => TrackingTestData.Json(empty),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.AddressValue);

        Assert.Equal(0, result.Address!.ConfirmedBalanceSats);
        Assert.Equal(0, result.Address.PendingBalanceChangeSats);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public async Task MempoolEntriesDoNotBecomeTheConfirmedPaginationCursor()
    {
        var confirmed = Enumerable.Range(1, 25).Select(index => TrackingTestData.Transaction(
            txId: index.ToString("x64"), confirmed: true)).ToArray();
        var next = TrackingTestData.Transaction(txId: new string('d', 64), confirmed: true);
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            var value when value.Contains("/txs/chain/", StringComparison.Ordinal) => TrackingTestData.Json(new[] { next }),
            var value when value.EndsWith("/txs/mempool", StringComparison.Ordinal) =>
                TrackingTestData.Json(new[] { TrackingTestData.Transaction() }),
            var value when value.EndsWith("/txs/chain", StringComparison.Ordinal) => TrackingTestData.Json(confirmed),
            _ => TrackingTestData.Json(TrackingTestData.Address()),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.AddressValue);
        var page = await service.GetAddressTransactionsAsync(result.Query, result.NextCursor!);

        Assert.Equal(26, result.Transactions.Count);
        Assert.Equal(confirmed[^1].TxId, result.NextCursor);
        Assert.Equal(next.TxId, Assert.Single(page.Transactions).TxId);
        Assert.Null(page.NextCursor);
        Assert.Contains($"address/{TrackingTestData.AddressValue}/txs/chain/{confirmed[^1].TxId}", handler.Paths);
    }

    [Fact]
    public async Task BusyAddressLoadsAnExplicitConfirmedPageAlongsideFiftyMempoolTransactions()
    {
        var pending = Enumerable.Range(1, 50).Select(index => TrackingTestData.Transaction(txId: index.ToString("x64"))).ToArray();
        var confirmed = Enumerable.Range(101, 25).Select(index => TrackingTestData.Transaction(
            txId: index.ToString("x64"), confirmed: true)).ToArray();
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            var value when value.EndsWith("/txs/mempool", StringComparison.Ordinal) => TrackingTestData.Json(pending),
            var value when value.EndsWith("/txs/chain", StringComparison.Ordinal) => TrackingTestData.Json(confirmed),
            var value when value.EndsWith("/txs", StringComparison.Ordinal) =>
                throw new InvalidOperationException("Combined history has provider-specific page limits."),
            _ => TrackingTestData.Json(TrackingTestData.Address()),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.AddressValue);

        Assert.Equal(75, result.Transactions.Count);
        Assert.Equal(50, result.Transactions.Count(transaction => !transaction.Confirmed));
        Assert.Equal(25, result.Transactions.Count(transaction => transaction.Confirmed));
        Assert.Equal(confirmed[^1].TxId, result.NextCursor);
        Assert.DoesNotContain(handler.Paths, path => path.EndsWith("/txs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmationBetweenHistoryReadsPrefersConfirmedTransactionWithoutDuplication()
    {
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            var value when value.EndsWith("/txs/mempool", StringComparison.Ordinal) =>
                TrackingTestData.Json(new[] { TrackingTestData.Transaction() }),
            var value when value.EndsWith("/txs/chain", StringComparison.Ordinal) =>
                TrackingTestData.Json(new[] { TrackingTestData.Transaction(confirmed: true) }),
            _ => TrackingTestData.Json(TrackingTestData.Address()),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.AddressValue);

        Assert.Equal(3, Assert.Single(result.Transactions).Confirmations);
    }

    [Fact]
    public async Task RefreshCanReturnPreviouslyConfirmedTransactionToMempool()
    {
        var time = new TrackingTestTimeProvider();
        var confirmed = true;
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            "block-height/100" => TrackingTestData.Text(TrackingTestData.BlockHash),
            _ => TrackingTestData.Json(TrackingTestData.Transaction(confirmed: confirmed)),
        });
        using var client = TrackingTestData.Client(handler, time: time);
        using var service = TrackingTestData.Service(client, time: time);

        var first = await service.LookupAsync(TrackingTestData.TxId);
        confirmed = false;
        var cached = await service.LookupAsync(TrackingTestData.TxId);
        time.Advance(TimeSpan.FromSeconds(31));
        var refreshed = await service.LookupAsync(TrackingTestData.TxId);

        Assert.Same(first, cached);
        Assert.Equal(3, first.Transaction!.Confirmations);
        Assert.False(refreshed.Transaction!.Confirmed);
        Assert.Equal(0, refreshed.Transaction.Confirmations);
        Assert.Null(refreshed.Transaction.BlockHash);
        Assert.True(refreshed.UpdatedAt > first.UpdatedAt);
    }

    [Fact]
    public async Task OrphanedBlockDoesNotReceiveConfirmationsWhenProviderRemainsStale()
    {
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            "block-height/100" => TrackingTestData.Text(new string('c', 64)),
            _ => TrackingTestData.Json(TrackingTestData.Transaction(confirmed: true)),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupAsync(TrackingTestData.TxId));

        Assert.Equal(BitcoinTrackingError.Unavailable, exception.Error);
        Assert.Equal(2, handler.Paths.Count(path => path == $"tx/{TrackingTestData.TxId}"));
    }

    [Fact]
    public async Task ReorgDuringLookupRereadsTransactionAndCanRecoverToPending()
    {
        var reads = 0;
        using var handler = new TrackingRoutingHandler(path => path switch
        {
            "blocks/tip/height" => TrackingTestData.Text("102"),
            "block-height/100" => TrackingTestData.Text(new string('c', 64)),
            _ => TrackingTestData.Json(TrackingTestData.Transaction(confirmed: ++reads == 1)),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.TxId);

        Assert.False(result.Transaction!.Confirmed);
        Assert.Equal(0, result.Transaction.Confirmations);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task CancellingOneReaderDoesNotCancelAnotherReadersSharedLookup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TrackingRoutingHandler(async (path, token) =>
        {
            if (path == "blocks/tip/height") return TrackingTestData.Text("102");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return TrackingTestData.Json(TrackingTestData.Transaction());
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);
        using var cancellation = new CancellationTokenSource();

        var first = service.LookupAsync(TrackingTestData.TxId, cancellation.Token);
        await entered.Task;
        var second = service.LookupAsync(TrackingTestData.TxId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        var result = await second;

        Assert.Equal(TrackingTestData.TxId, result.Transaction!.TxId);
        Assert.Equal(1, handler.Paths.Count(path => path.StartsWith("tx/", StringComparison.Ordinal)));
        Assert.Same(result, await service.LookupAsync(TrackingTestData.TxId));
    }

    [Fact]
    public async Task InvalidInputAndCursorNeverReachProvider()
    {
        using var handler = new TrackingRoutingHandler(_ => throw new InvalidOperationException("No request expected."));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var invalid = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupAsync("https://private.invalid/"));
        var cursor = await Assert.ThrowsAsync<BitcoinTrackingException>(() =>
            service.GetAddressTransactionsAsync(TrackingTestData.AddressValue, "../arbitrary"));

        Assert.Equal(BitcoinTrackingError.InvalidInput, invalid.Error);
        Assert.Equal(BitcoinTrackingError.InvalidInput, cursor.Error);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ProviderFailuresAreSharedBrieflyWithoutCachingAFalseEmptyResult()
    {
        using var handler = new TrackingRoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No such mempool transaction"),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var first = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupAsync(TrackingTestData.TxId));
        var second = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupAsync(TrackingTestData.TxId));

        Assert.Equal(BitcoinTrackingError.NotFound, first.Error);
        Assert.Equal(BitcoinTrackingError.NotFound, second.Error);
        Assert.Single(handler.Paths);
        Assert.DoesNotContain(TrackingTestData.TxId, first.Message);
    }

    [Fact]
    public async Task ProviderNullStatusIsUnavailableRatherThanAZeroConfirmationResult()
    {
        using var handler = new TrackingRoutingHandler(_ => TrackingTestData.Json(
            TrackingTestData.Transaction() with { Status = null! }));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var exception = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupAsync(TrackingTestData.TxId));

        Assert.Equal(BitcoinTrackingError.Unavailable, exception.Error);
    }

    [Fact]
    public async Task WalletTotalsUseCompleteAddressStatisticsAndDeduplicateAnInternalTransfer()
    {
        var transfer = TrackingTestData.Transaction(confirmed: true) with
        {
            Fee = 5_000,
            Outputs = [new EsploraOutput { Address = TrackingTestData.OtherAddress, Value = 95_000 }],
        };
        var summaries = new Dictionary<string, EsploraAddress>
        {
            [TrackingTestData.AddressValue] = WalletAddress(TrackingTestData.AddressValue, 9_000_000, 8_000_000, 10_000, 20_000),
            [TrackingTestData.OtherAddress] = WalletAddress(TrackingTestData.OtherAddress, 7_000_000, 3_000_000, 30_000, 10_000),
        };
        using var handler = WalletHandler((_, _) => [transfer], summaries);
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupWalletAsync([TrackingTestData.AddressValue, TrackingTestData.OtherAddress]);

        Assert.Equal(BitcoinTrackingKind.Wallet, result.Kind);
        Assert.Null(result.Address);
        Assert.Equal(2, result.Wallet!.AddressCount);
        Assert.Equal(16_000_000L, result.Wallet.ConfirmedReceivedSats);
        Assert.Equal(11_000_000L, result.Wallet.ConfirmedSpentSats);
        Assert.Equal(40_000L, result.Wallet.PendingReceivedSats);
        Assert.Equal(30_000L, result.Wallet.PendingSpentSats);
        Assert.Equal(5_000_000L, result.Wallet.ConfirmedBalanceSats);
        Assert.Equal(10_000L, result.Wallet.PendingBalanceChangeSats);
        Assert.Equal(5_010_000L, result.Wallet.TotalBalanceSats);
        var transaction = Assert.Single(result.Transactions);
        Assert.Equal(95_000L, transaction.TrackedReceivedSats);
        Assert.Equal(100_000L, transaction.TrackedSpentSats);
        Assert.Equal(-5_000L, transaction.TrackedNetSats);
        Assert.Equal(0L, transaction.IncomingSats);
        Assert.Equal(5_000L, transaction.OutgoingSats);
        Assert.Null(transaction.AddressNetSats);
    }

    [Fact]
    public async Task SingleAddressAlsoExposesFullReceivedSpentTotalsAndTrackedAmounts()
    {
        using var handler = WalletHandler((_, _) => [TrackingTestData.Transaction(confirmed: true)]);
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupAsync(TrackingTestData.AddressValue);

        Assert.Equal(100_000L, result.Address!.ConfirmedReceivedSats);
        Assert.Equal(0L, result.Address.ConfirmedSpentSats);
        Assert.Equal(9_500L, result.Address.PendingReceivedSats);
        Assert.Equal(100_000L, result.Address.PendingSpentSats);
        Assert.Equal(9_500L, result.Address.TotalBalanceSats);
        Assert.Equal(new[] { TrackingTestData.AddressValue }, result.TrackedAddresses);
        var transaction = Assert.Single(result.Transactions);
        Assert.Equal(9_500L, transaction.TrackedReceivedSats);
        Assert.Equal(100_000L, transaction.TrackedSpentSats);
        Assert.Equal(transaction.AddressNetSats, transaction.TrackedNetSats);
    }

    [Fact]
    public async Task WalletPaginationPreservesUnconsumedAddressPrefixesAndDeduplicatesAcrossPages()
    {
        var shared = TrackingTestData.Transaction(txId: new string('0', 64), confirmed: true) with
        {
            Status = TrackingTestData.Transaction(confirmed: true).Status with { BlockHeight = 200 },
        };
        var first = Enumerable.Range(1, 25).Select(index => TrackingTestData.Transaction(index.ToString("x64"), true) with
        {
            Status = shared.Status,
        }).ToArray();
        using var handler = WalletHandler((address, cursor) => address switch
        {
            TrackingTestData.AddressValue when cursor is null => first,
            TrackingTestData.AddressValue when cursor == first[23].TxId => [first[24], shared],
            TrackingTestData.OtherAddress when cursor is null => [shared],
            _ => throw new InvalidOperationException("A wallet cursor skipped unconsumed history."),
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);
        var addresses = new[] { TrackingTestData.AddressValue, TrackingTestData.OtherAddress };

        var initial = await service.LookupWalletAsync(addresses);
        var page = await service.GetWalletTransactionsAsync(addresses, initial.NextCursor!);

        Assert.Equal(25, initial.Transactions.Count);
        Assert.Contains(initial.Transactions, transaction => transaction.TxId == shared.TxId);
        Assert.Equal(first[24].TxId, Assert.Single(page.Transactions).TxId);
        Assert.DoesNotContain(page.Transactions, transaction => initial.Transactions.Any(previous => previous.TxId == transaction.TxId));
        Assert.Null(page.NextCursor);
        Assert.Contains($"address/{TrackingTestData.AddressValue}/txs/chain/{first[23].TxId}", handler.Paths);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("w1_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmationBetweenAddressSnapshotsPrefersConfirmedWalletRepresentation()
    {
        using var handler = WalletHandler(
            (address, _) => address == TrackingTestData.OtherAddress ? [TrackingTestData.Transaction(confirmed: true)] : [],
            pending: address => address == TrackingTestData.AddressValue ? [TrackingTestData.Transaction()] : []);
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var result = await service.LookupWalletAsync([TrackingTestData.AddressValue, TrackingTestData.OtherAddress]);

        var transaction = Assert.Single(result.Transactions);
        Assert.True(transaction.Confirmed);
        Assert.Equal(201, transaction.Confirmations);
    }

    [Fact]
    public async Task WalletCursorRejectsAnotherAddressGroupAndARefreshedSnapshot()
    {
        var time = new TrackingTestTimeProvider();
        var chain = Enumerable.Range(1, 25).Select(index => TrackingTestData.Transaction(index.ToString("x64"), true)).ToArray();
        using var handler = WalletHandler((_, _) => chain);
        using var client = TrackingTestData.Client(handler, time: time);
        using var service = TrackingTestData.Service(client, time: time);
        var addresses = new[] { TrackingTestData.AddressValue, TrackingTestData.OtherAddress };

        var initial = await service.LookupWalletAsync(addresses);
        var wrongGroup = await Assert.ThrowsAsync<BitcoinTrackingException>(() =>
            service.GetWalletTransactionsAsync([TrackingTestData.AddressValue], initial.NextCursor!));
        time.Advance(TimeSpan.FromSeconds(31));
        var refreshed = await service.LookupWalletAsync(addresses);
        var stale = await Assert.ThrowsAsync<BitcoinTrackingException>(() =>
            service.GetWalletTransactionsAsync(addresses, initial.NextCursor!));

        Assert.NotEqual(initial.NextCursor, refreshed.NextCursor);
        Assert.Equal(BitcoinTrackingError.InvalidInput, wrongGroup.Error);
        Assert.Equal(BitcoinTrackingError.InvalidInput, stale.Error);
    }

    [Fact]
    public async Task WalletCursorExpiresAndNeverBecomesAProviderRequestParameter()
    {
        var time = new TrackingTestTimeProvider();
        var chain = Enumerable.Range(1, 25).Select(index => TrackingTestData.Transaction(index.ToString("x64"), true)).ToArray();
        using var handler = WalletHandler((_, _) => chain);
        using var client = TrackingTestData.Client(handler, time: time);
        using var service = TrackingTestData.Service(client, time: time);
        var addresses = new[] { TrackingTestData.AddressValue };
        var initial = await service.LookupWalletAsync(addresses);
        var calls = handler.Paths.Count;
        time.Advance(TimeSpan.FromMinutes(11));

        var expired = await Assert.ThrowsAsync<BitcoinTrackingException>(() =>
            service.GetWalletTransactionsAsync(addresses, initial.NextCursor!));

        Assert.Equal(BitcoinTrackingError.InvalidInput, expired.Error);
        Assert.Equal(calls, handler.Paths.Count);
    }

    [Fact]
    public async Task WalletNormalizesDuplicateAddressesAndSharesOrderIndependentSnapshots()
    {
        using var handler = WalletHandler((_, _) => [TrackingTestData.Transaction(confirmed: true)]);
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var first = await service.LookupWalletAsync([TrackingTestData.OtherAddress, TrackingTestData.AddressValue, TrackingTestData.AddressValue]);
        var second = await service.LookupWalletAsync([TrackingTestData.AddressValue, TrackingTestData.OtherAddress]);

        Assert.Equal(2, first.TrackedAddresses.Count);
        Assert.Same(first, second);
        Assert.Equal(9, handler.Paths.Count);
    }

    [Fact]
    public async Task WalletRejectsTransactionIdsInvalidAddressesAndTooManyAddressesBeforeProviderAccess()
    {
        using var handler = new TrackingRoutingHandler(_ => throw new InvalidOperationException("No request expected."));
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);
        foreach (var addresses in new[]
        {
            Array.Empty<string>(), new[] { TrackingTestData.TxId }, new[] { "xpub-not-supported" },
            Enumerable.Repeat(TrackingTestData.AddressValue, BitcoinTrackingService.MaxWalletAddresses + 1).ToArray(),
        })
        {
            var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupWalletAsync(addresses));
            Assert.Equal(BitcoinTrackingError.InvalidInput, error.Error);
        }
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task AFailedWalletAddressDoesNotReturnAnIncompleteBalanceAsTheWalletTotal()
    {
        using var good = WalletHandler((_, _) => []);
        using var handler = new TrackingRoutingHandler(async (path, token) =>
        {
            if (path == $"address/{TrackingTestData.OtherAddress}")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            using var invoker = new HttpMessageInvoker(good, disposeHandler: false);
            return await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://provider.invalid/api/" + path), token);
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() =>
            service.LookupWalletAsync([TrackingTestData.AddressValue, TrackingTestData.OtherAddress]));

        Assert.Equal(BitcoinTrackingError.Unavailable, error.Error);
    }

    [Fact]
    public void WalletAccountingCountsAllOwnedInputsAndOutputsWhileKeepingMissingInputsUnknown()
    {
        var transaction = new BitcoinTransaction
        {
            TxId = TrackingTestData.TxId,
            Inputs = [new() { Address = TrackingTestData.AddressValue, ValueSats = 100_000 },
                new() { Address = TrackingTestData.OtherAddress, ValueSats = 50_000 }],
            Outputs = [new() { Address = TrackingTestData.AddressValue, ValueSats = 20_000 },
                new() { Address = TrackingTestData.OtherAddress, ValueSats = 30_000 },
                new() { Address = TrackingTestData.AddressValue, ValueSats = 5_000 },
                new() { Address = "untracked", ValueSats = 94_000 }],
            FeeSats = 1_000,
        };
        var addresses = new[] { TrackingTestData.AddressValue, TrackingTestData.OtherAddress };

        var tracked = BitcoinWalletAccounting.ApplyContext(transaction, addresses);
        var missing = BitcoinWalletAccounting.ApplyContext(transaction with { Inputs = [new() { Address = TrackingTestData.AddressValue }] }, addresses);
        var partial = BitcoinWalletAccounting.ApplyContext(transaction, [TrackingTestData.OtherAddress]);
        var incoming = BitcoinWalletAccounting.ApplyContext(transaction with
        {
            Inputs = [new() { Address = "untracked", ValueSats = 100_000 }],
            Outputs = [new() { Address = TrackingTestData.OtherAddress, ValueSats = 99_000 }],
        }, [TrackingTestData.OtherAddress]);

        Assert.Equal(55_000L, tracked.TrackedReceivedSats);
        Assert.Equal(150_000L, tracked.TrackedSpentSats);
        Assert.Equal(-95_000L, tracked.TrackedNetSats);
        Assert.Equal(95_000L, tracked.OutgoingSats);
        Assert.Equal(55_000L, missing.TrackedReceivedSats);
        Assert.Null(missing.TrackedSpentSats);
        Assert.Null(missing.TrackedNetSats);
        Assert.Null(missing.IncomingSats);
        Assert.Equal(-20_000L, partial.TrackedNetSats);
        Assert.Equal(99_000L, incoming.TrackedNetSats);
        Assert.Equal(99_000L, incoming.IncomingSats);
        Assert.Equal(0L, incoming.OutgoingSats);
    }

    [Theory]
    [InlineData("balance")]
    [InlineData("received")]
    [InlineData("spent")]
    public async Task AddressCombinedTotalsOverflowIsUnavailableBeforeTheSnapshotReachesTheUi(string total)
    {
        var summary = total switch
        {
            "balance" => WalletAddress(TrackingTestData.AddressValue, long.MaxValue, 0, 1, 0),
            "received" => WalletAddress(TrackingTestData.AddressValue, long.MaxValue, long.MaxValue, 1, 1),
            _ => WalletAddress(TrackingTestData.AddressValue, long.MaxValue, long.MaxValue, 0, 1),
        };
        using var handler = WalletHandler((_, _) => [], new Dictionary<string, EsploraAddress>
        {
            [TrackingTestData.AddressValue] = summary,
        });
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupAsync(TrackingTestData.AddressValue));

        Assert.Equal(BitcoinTrackingError.Unavailable, error.Error);
    }

    [Theory]
    [InlineData("balance")]
    [InlineData("received")]
    [InlineData("spent")]
    public async Task WalletCombinedTotalsOverflowIsUnavailableEvenWhenEachAddressTotalFits(string total)
    {
        var half = long.MaxValue / 2;
        var addresses = new[] { TrackingTestData.AddressValue, TrackingTestData.OtherAddress };
        var summaries = addresses.ToDictionary(address => address, address => total switch
        {
            "balance" => WalletAddress(address, half, 0, 2, 0),
            "received" => WalletAddress(address, half, half, 1, 1),
            _ => WalletAddress(address, half, half, 0, 2),
        });
        using var handler = WalletHandler((_, _) => [], summaries);
        using var client = TrackingTestData.Client(handler);
        using var service = TrackingTestData.Service(client);

        var error = await Assert.ThrowsAsync<BitcoinTrackingException>(() => service.LookupWalletAsync(addresses));

        Assert.Equal(BitcoinTrackingError.Unavailable, error.Error);
    }

    private static EsploraAddress WalletAddress(string address, long received, long spent, long pendingReceived, long pendingSpent) => new()
    {
        Address = address,
        Chain = new EsploraAddressStats { FundedSats = received, SpentSats = spent, TransactionCount = 5_000 },
        Mempool = new EsploraAddressStats { FundedSats = pendingReceived, SpentSats = pendingSpent, TransactionCount = 20 },
    };

    private static TrackingRoutingHandler WalletHandler(Func<string, string?, EsploraTransaction[]> chain,
        Dictionary<string, EsploraAddress>? summaries = null, Func<string, EsploraTransaction[]>? pending = null) => new(path =>
    {
        if (path == "blocks/tip/height") return TrackingTestData.Text("300");
        var parts = path.Split('/');
        var address = parts[1];
        if (parts.Length == 2)
            return TrackingTestData.Json(summaries?.GetValueOrDefault(address) ?? (TrackingTestData.Address() with { Address = address }));
        if (parts[3] == "mempool") return TrackingTestData.Json(pending?.Invoke(address) ?? []);
        return TrackingTestData.Json(chain(address, parts.Length == 5 ? parts[4] : null));
    });
}

internal static class TrackingTestData
{
    internal const string AddressValue = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
    internal const string OtherAddress = "1BoatSLRHtKNngkdXEeobR76b53LETtpyT";
    internal static readonly string TxId = new('a', 64);
    internal static readonly string BlockHash = new('b', 64);

    internal static EsploraTransaction Transaction(string? txId = null, bool confirmed = false) => new()
    {
        TxId = txId ?? TxId, Fee = 500, Size = 200, Weight = 600,
        Inputs = [new EsploraInput
        {
            IsCoinbase = false, PreviousOutput = new EsploraOutput { Address = AddressValue, Value = 100_000 },
        }],
        Outputs = [new EsploraOutput { Address = OtherAddress, Value = 90_000 },
            new EsploraOutput { Address = AddressValue, Value = 9_500 }],
        Status = new EsploraStatus
        {
            Confirmed = confirmed, BlockHeight = confirmed ? 100 : null,
            BlockHash = confirmed ? BlockHash : null, BlockTime = confirmed ? 1_700_000_000 : null,
        },
    };

    internal static EsploraAddress Address() => new()
    {
        Address = AddressValue,
        Chain = new EsploraAddressStats { FundedSats = 100_000, SpentSats = 0, TransactionCount = 1 },
        Mempool = new EsploraAddressStats { FundedSats = 9_500, SpentSats = 100_000, TransactionCount = 1 },
    };

    internal static HttpResponseMessage Json<T>(T value) => Text(JsonSerializer.Serialize(value), "application/json");
    internal static HttpResponseMessage Text(string value, string mediaType = "text/plain") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, mediaType),
    };

    internal static EsploraClient Client(HttpMessageHandler handler, BitcoinTrackingOptions? options = null, TimeProvider? time = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://provider.invalid/api/") },
            Microsoft.Extensions.Options.Options.Create(options ?? new BitcoinTrackingOptions()), time ?? TimeProvider.System);

    internal static BitcoinTrackingService Service(EsploraClient client, BitcoinTrackingOptions? options = null, TimeProvider? time = null) =>
        new(client, Microsoft.Extensions.Options.Options.Create(options ?? new BitcoinTrackingOptions()), time ?? TimeProvider.System);
}

internal sealed class TrackingRoutingHandler : HttpMessageHandler
{
    private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _respond;
    internal ConcurrentQueue<string> Paths { get; } = new();

    internal TrackingRoutingHandler(Func<string, HttpResponseMessage> respond)
        : this((path, _) => Task.FromResult(respond(path))) { }
    internal TrackingRoutingHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath["/api/".Length..];
        Paths.Enqueue(path);
        Assert.Equal(HttpMethod.Get, request.Method);
        return _respond(path, cancellationToken);
    }
}

internal sealed class TrackingTestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan duration) => _now += duration;
}
