using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FreedomBlaze.Clients.BitcoinTracking;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Helpers;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models.BitcoinTracking;
using FreedomBlaze.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>
/// Bounded, short-lived mainnet snapshots shared across callers. Each caller may stop waiting without
/// cancelling another reader's request; shared provider work has its own timeout.
/// </summary>
public sealed class BitcoinTrackingService : IBitcoinTrackingService, IDisposable
{
    public const int MaxWalletAddresses = 10;
    private static readonly TimeSpan WalletCursorLifetime = TimeSpan.FromMinutes(10);
    private readonly EsploraClient _client;
    private readonly BitcoinTrackingOptions _options;
    private readonly TimeProvider _time;
    private readonly MemoryCache _cache;
    private readonly MemoryCache _walletCursors;
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _inFlight = new(StringComparer.Ordinal);

    public BitcoinTrackingService(EsploraClient client, IOptions<BitcoinTrackingOptions> options, TimeProvider time)
    {
        _client = client;
        _options = options.Value;
        _time = time;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxCacheEntries });
        // Cursor weight counts retained transaction IDs, preventing long browsing sessions from
        // retaining unbounded deduplication sets. IDs never leave this server-side cache.
        _walletCursors = new MemoryCache(new MemoryCacheOptions { SizeLimit = checked(_options.MaxCacheEntries * 100L) });
    }

    public Task<BitcoinTrackingResult> LookupAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!BitcoinTrackingInput.TryParse(query, out var kind, out var normalized))
            throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
        return GetSharedAsync<BitcoinTrackingResult>($"lookup:{normalized}",
            token => FetchLookupAsync(normalized, kind, token), cancellationToken);
    }

    public Task<BitcoinTransactionPage> GetAddressTransactionsAsync(string address, string lastSeenTxId,
        CancellationToken cancellationToken = default)
    {
        if (!BitcoinTrackingInput.TryParse(address, out var kind, out var normalized)
            || kind != BitcoinTrackingKind.Address
            || !BitcoinTrackingInput.TryParse(lastSeenTxId, out var cursorKind, out var cursor)
            || cursorKind != BitcoinTrackingKind.Transaction)
            throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
        return GetSharedAsync<BitcoinTransactionPage>($"page:{normalized}:{cursor}",
            token => FetchPageAsync(normalized, cursor, token), cancellationToken);
    }

    public Task<BitcoinTrackingResult> LookupWalletAsync(IReadOnlyCollection<string> addresses,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWalletAddresses(addresses);
        var group = WalletGroup(normalized);
        return GetSharedAsync<BitcoinTrackingResult>($"wallet:{group}",
            token => FetchWalletAsync(normalized, group, token), cancellationToken, WalletTimeout(normalized.Length));
    }

    public Task<BitcoinTransactionPage> GetWalletTransactionsAsync(IReadOnlyCollection<string> addresses, string cursor,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWalletAddresses(addresses);
        var group = WalletGroup(normalized);
        if (cursor is not { Length: 35 } || !cursor.StartsWith("w1_", StringComparison.Ordinal)
            || !cursor.AsSpan(3).ToArray().All(Uri.IsHexDigit)
            || !_walletCursors.TryGetValue<WalletCursorState>(cursor, out var state) || state is null
            || state.Group != group || state.ExpiresAt <= _time.GetUtcNow()
            || !IsCurrentGeneration(state))
            throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
        return GetSharedAsync<BitcoinTransactionPage>($"wallet-page:{cursor}",
            token => FetchWalletPageAsync(state, token), cancellationToken, WalletTimeout(normalized.Length));
    }

    private async Task<T> GetSharedAsync<T>(string key, Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken, TimeSpan? operationTimeout = null) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheEntry entry;
        if (TryReadCache(key, out var cached))
            entry = cached!;
        else
        {
            if (_inFlight.Count >= _options.MaxCacheEntries && !_inFlight.ContainsKey(key))
                throw new BitcoinTrackingException(BitcoinTrackingError.RateLimited);
            var shared = _inFlight.GetOrAdd(key, _ => new Lazy<Task<CacheEntry>>(
                () => FetchAndCacheAsync(key, fetch, operationTimeout), LazyThreadSafetyMode.ExecutionAndPublication));
            entry = await shared.Value.WaitAsync(cancellationToken);
        }
        if (entry.Error is { } error)
            throw new BitcoinTrackingException(error);
        return (T)entry.Value!;
    }

    private bool TryReadCache(string key, out CacheEntry? entry)
    {
        if (_cache.TryGetValue(key, out entry) && entry is not null && _time.GetUtcNow() < entry.ExpiresAt)
            return true;
        entry = null;
        return false;
    }

    private async Task<CacheEntry> FetchAndCacheAsync<T>(string key, Func<CancellationToken, Task<T>> fetch, TimeSpan? operationTimeout)
        where T : class
    {
        try
        {
            if (TryReadCache(key, out var cached))
                return cached!;
            using var timeout = new CancellationTokenSource(operationTimeout ?? _options.RequestTimeout, _time);
            CacheEntry entry;
            try
            {
                entry = new CacheEntry(await fetch(timeout.Token), null, _time.GetUtcNow() + _options.CacheDuration);
            }
            catch (BitcoinTrackingException exception)
            {
                entry = new CacheEntry(null, exception.Error, _time.GetUtcNow() + _options.FailureCooldown);
            }
            catch (Exception exception) when (exception is OperationCanceledException or OverflowException or ArgumentOutOfRangeException)
            {
                entry = new CacheEntry(null, BitcoinTrackingError.Unavailable, _time.GetUtcNow() + _options.FailureCooldown);
            }
            _cache.Set(key, entry, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = entry.Error is null ? _options.CacheDuration : _options.FailureCooldown,
            });
            return entry;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<BitcoinTrackingResult> FetchLookupAsync(string query, BitcoinTrackingKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == BitcoinTrackingKind.Transaction)
        {
            var transaction = await _client.GetTransactionAsync(query, cancellationToken);
            ValidateTransaction(transaction);
            if (!string.Equals(transaction.TxId, query, StringComparison.OrdinalIgnoreCase))
                throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
            var tip = await _client.GetTipHeightAsync(cancellationToken);
            if (transaction.Status.Confirmed)
            {
                if (transaction.Status.BlockHeight > tip)
                    throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
                var currentHash = await _client.GetBestChainHashAsync(transaction.Status.BlockHeight!.Value, cancellationToken);
                if (!string.Equals(currentHash, transaction.Status.BlockHash, StringComparison.OrdinalIgnoreCase))
                {
                    // A reorg can happen between calls. Re-read status through /tx, and do not present
                    // confirmations for an orphaned block if the provider has not caught up yet.
                    transaction = await _client.GetTransactionAsync(query, cancellationToken);
                    ValidateTransaction(transaction);
                    tip = await _client.GetTipHeightAsync(cancellationToken);
                    if (!string.Equals(transaction.TxId, query, StringComparison.OrdinalIgnoreCase))
                        throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
                    if (transaction.Status.Confirmed
                        && (transaction.Status.BlockHeight > tip
                            || !string.Equals(await _client.GetBestChainHashAsync(transaction.Status.BlockHeight!.Value, cancellationToken),
                                transaction.Status.BlockHash, StringComparison.OrdinalIgnoreCase)))
                        throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
                }
            }
            return new BitcoinTrackingResult
            {
                Query = query, Kind = kind, Transaction = MapTransaction(transaction, tip),
                TipHeight = tip, UpdatedAt = _time.GetUtcNow(),
            };
        }

        var addressTask = _client.GetAddressAsync(query, cancellationToken);
        var mempoolTask = _client.GetAddressMempoolTransactionsAsync(query, cancellationToken);
        await Task.WhenAll(addressTask, mempoolTask);
        var address = await addressTask;
        var mempool = await mempoolTask;
        // The combined /txs endpoint has provider-specific limits (mempool.space currently returns
        // 50 total rather than Esplora's 50 mempool + 25 confirmed). Explicit endpoints preserve
        // the confirmed page boundary even when the address has many pending transactions.
        var confirmed = await _client.GetAddressTransactionsAsync(query, null, cancellationToken);
        ValidateAddress(address, query);
        ValidateHistory(mempool, 50, chainOnly: false);
        ValidateHistory(confirmed, EsploraClient.ConfirmedPageSize, chainOnly: true);
        // A transaction may confirm between the two reads. Prefer its confirmed representation.
        var confirmedIds = confirmed.Select(tx => tx.TxId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transactions = mempool.Where(tx => !confirmedIds.Contains(tx.TxId)).Concat(confirmed).ToArray();
        var height = await _client.GetTipHeightAsync(cancellationToken);
        return new BitcoinTrackingResult
        {
            Query = query,
            Kind = kind,
            Address = new BitcoinAddressSummary
            {
                ConfirmedBalanceSats = checked(address.Chain.FundedSats - address.Chain.SpentSats),
                PendingBalanceChangeSats = checked(address.Mempool.FundedSats - address.Mempool.SpentSats),
                ConfirmedReceivedSats = address.Chain.FundedSats,
                ConfirmedSpentSats = address.Chain.SpentSats,
                PendingReceivedSats = address.Mempool.FundedSats,
                PendingSpentSats = address.Mempool.SpentSats,
                ConfirmedTransactionCount = address.Chain.TransactionCount,
                PendingTransactionCount = address.Mempool.TransactionCount,
            },
            TrackedAddresses = Array.AsReadOnly(new[] { query }),
            // Each list response carries fresh current-chain statuses. They are only retained for
            // CacheDuration, so refreshed pages can lower confirmations or return a tx to the mempool.
            Transactions = Array.AsReadOnly(transactions.Select(tx => MapTransaction(tx, height, query)).ToArray()),
            NextCursor = NextCursor(confirmed),
            TipHeight = height,
            UpdatedAt = _time.GetUtcNow(),
        };
    }

    private async Task<BitcoinTrackingResult> FetchWalletAsync(string[] addresses, string group, CancellationToken cancellationToken)
    {
        var snapshots = new List<BitcoinTrackingResult>(addresses.Length);
        var parallel = Math.Max(1, _options.MaxConcurrentRequests / 2);
        for (var offset = 0; offset < addresses.Length; offset += parallel)
            snapshots.AddRange(await Task.WhenAll(addresses.Skip(offset).Take(parallel)
                .Select(address => LookupAsync(address, cancellationToken))));
        var tip = await _client.GetTipHeightAsync(cancellationToken);
        var wallet = new BitcoinWalletSummary
        {
            AddressCount = addresses.Length,
            ConfirmedBalanceSats = snapshots.Sum(snapshot => snapshot.Address!.ConfirmedBalanceSats),
            PendingBalanceChangeSats = snapshots.Sum(snapshot => snapshot.Address!.PendingBalanceChangeSats),
            ConfirmedReceivedSats = snapshots.Sum(snapshot => snapshot.Address!.ConfirmedReceivedSats),
            ConfirmedSpentSats = snapshots.Sum(snapshot => snapshot.Address!.ConfirmedSpentSats),
            PendingReceivedSats = snapshots.Sum(snapshot => snapshot.Address!.PendingReceivedSats),
            PendingSpentSats = snapshots.Sum(snapshot => snapshot.Address!.PendingSpentSats),
        };
        ValidateCombinedTotals(wallet.ConfirmedBalanceSats, wallet.PendingBalanceChangeSats,
            wallet.ConfirmedReceivedSats, wallet.PendingReceivedSats, wallet.ConfirmedSpentSats, wallet.PendingSpentSats);
        var generation = Guid.NewGuid().ToString("N");
        var expires = _time.GetUtcNow() + WalletCursorLifetime;
        _walletCursors.Set($"group:{group}", new WalletGeneration(generation, expires),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = WalletCursorLifetime });
        var allTransactions = snapshots.SelectMany(snapshot => snapshot.Transactions).ToArray();
        var confirmedIds = allTransactions.Where(transaction => transaction.Confirmed)
            .Select(transaction => transaction.TxId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = allTransactions.Where(transaction => !transaction.Confirmed && !confirmedIds.Contains(transaction.TxId))
            .DistinctBy(transaction => transaction.TxId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(transaction => transaction.TxId, StringComparer.Ordinal)
            .Select(transaction => BitcoinWalletAccounting.ApplyContext(transaction, addresses)).ToArray();
        var seen = pending.Select(transaction => transaction.TxId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var histories = snapshots.Select(snapshot => new WalletAddressHistory(
            new WalletAddressPosition(snapshot.Query, null, true),
            snapshot.Transactions.Where(transaction => transaction.Confirmed).ToArray())).ToArray();
        var merged = MergeWalletHistory(histories, seen, tip, addresses);
        var cursor = StoreWalletCursor(group, generation, addresses, merged.Positions, seen, expires);
        return new BitcoinTrackingResult
        {
            Query = string.Join('\n', addresses), Kind = BitcoinTrackingKind.Wallet,
            TrackedAddresses = Array.AsReadOnly(addresses), Wallet = wallet,
            Transactions = Array.AsReadOnly(pending.Concat(merged.Transactions).ToArray()),
            NextCursor = cursor, TipHeight = tip, UpdatedAt = _time.GetUtcNow(),
        };
    }

    private async Task<BitcoinTransactionPage> FetchWalletPageAsync(WalletCursorState state, CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(state))
            throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
        var pages = new List<(WalletAddressPosition Position, EsploraTransaction[] Transactions)>(state.Positions.Length);
        // Read current statuses on every new page; the cursor retains positions and IDs, not stale
        // transaction bodies. Re-querying unconsumed prefixes avoids skipping a different address's history.
        foreach (var position in state.Positions)
        {
            if (!position.HasMore)
            {
                pages.Add((position, []));
                continue;
            }
            var transactions = await _client.GetAddressTransactionsAsync(position.Address, position.LastSeenTxId, cancellationToken);
            ValidateHistory(transactions, EsploraClient.ConfirmedPageSize, chainOnly: true);
            if (position.LastSeenTxId is { } previous
                && transactions.Any(transaction => string.Equals(transaction.TxId, previous, StringComparison.OrdinalIgnoreCase)))
                throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
            pages.Add((position, transactions));
        }
        var tip = await _client.GetTipHeightAsync(cancellationToken);
        var histories = pages.Select(page => new WalletAddressHistory(page.Position,
            page.Transactions.Select(transaction => MapTransaction(transaction, tip)).ToArray())).ToArray();
        var seen = state.SeenTxIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = MergeWalletHistory(histories, seen, tip, state.Addresses);
        var cursor = StoreWalletCursor(state.Group, state.Generation, state.Addresses, merged.Positions, seen, state.ExpiresAt);
        return new BitcoinTransactionPage
        {
            Transactions = Array.AsReadOnly(merged.Transactions), NextCursor = cursor,
            TipHeight = tip, UpdatedAt = _time.GetUtcNow(),
        };
    }

    private static WalletMerge MergeWalletHistory(IReadOnlyList<WalletAddressHistory> histories, HashSet<string> seen,
        int tip, string[] addresses)
    {
        var indexes = new int[histories.Count];
        var result = new List<BitcoinTransaction>(EsploraClient.ConfirmedPageSize);
        while (result.Count < EsploraClient.ConfirmedPageSize)
        {
            for (var index = 0; index < histories.Count; index++)
                while (indexes[index] < histories[index].Transactions.Length
                    && seen.Contains(histories[index].Transactions[indexes[index]].TxId))
                    indexes[index]++;
            var heads = Enumerable.Range(0, histories.Count)
                .Where(index => indexes[index] < histories[index].Transactions.Length).ToArray();
            if (heads.Length == 0) break;
            // Advance only source heads, preserving each Esplora page's order even for transactions
            // in the same block. A cursor never advances beyond an unconsumed transaction.
            var selected = heads.OrderByDescending(index => histories[index].Transactions[indexes[index]].BlockHeight)
                .ThenBy(index => histories[index].Transactions[indexes[index]].TxId, StringComparer.Ordinal).First();
            var transaction = histories[selected].Transactions[indexes[selected]++];
            if (transaction.BlockHeight is not { } height || height > tip)
                throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
            transaction = transaction with { Confirmations = checked(tip - height + 1) };
            seen.Add(transaction.TxId);
            result.Add(BitcoinWalletAccounting.ApplyContext(transaction, addresses));
        }
        // Consume duplicated heads even when the visible page is full, so the next cursor does not
        // needlessly reload those copies from another tracked address.
        var positions = histories.Select((history, index) =>
        {
            while (indexes[index] < history.Transactions.Length && seen.Contains(history.Transactions[indexes[index]].TxId))
                indexes[index]++;
            return history.Position with
            {
                LastSeenTxId = indexes[index] > 0 ? history.Transactions[indexes[index] - 1].TxId : history.Position.LastSeenTxId,
                HasMore = indexes[index] < history.Transactions.Length || history.Transactions.Length == EsploraClient.ConfirmedPageSize,
            };
        }).ToArray();
        return new WalletMerge(result.ToArray(), positions);
    }

    private string? StoreWalletCursor(string group, string generation, string[] addresses, WalletAddressPosition[] positions,
        HashSet<string> seen, DateTimeOffset expires)
    {
        if (!positions.Any(position => position.HasMore)) return null;
        var state = new WalletCursorState(group, generation, addresses, positions, seen.ToArray(), expires);
        if (!IsCurrentGeneration(state) || expires <= _time.GetUtcNow())
            throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
        var weight = checked(1L + seen.Count + addresses.Length * 5L);
        if (weight > _options.MaxCacheEntries * 100L)
            throw new BitcoinTrackingException(BitcoinTrackingError.RateLimited);
        var cursor = "w1_" + Guid.NewGuid().ToString("N");
        _walletCursors.Set(cursor, state, new MemoryCacheEntryOptions
        {
            Size = weight, AbsoluteExpirationRelativeToNow = expires - _time.GetUtcNow(),
        });
        return cursor;
    }

    private bool IsCurrentGeneration(WalletCursorState state) =>
        _walletCursors.TryGetValue<WalletGeneration>($"group:{state.Group}", out var generation)
        && generation is not null && generation.Id == state.Generation && generation.ExpiresAt > _time.GetUtcNow();

    private TimeSpan WalletTimeout(int addressCount)
    {
        var parallel = Math.Max(1, _options.MaxConcurrentRequests / 2);
        var batches = Math.Max(1, (addressCount + parallel - 1) / parallel);
        return TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(2).Ticks, _options.RequestTimeout.Ticks * batches));
    }

    private static string[] NormalizeWalletAddresses(IReadOnlyCollection<string> addresses)
    {
        if (addresses is null || addresses.Count is < 1 or > MaxWalletAddresses)
            throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (!BitcoinTrackingInput.TryParse(address, out var kind, out var value) || kind != BitcoinTrackingKind.Address)
                throw new BitcoinTrackingException(BitcoinTrackingError.InvalidInput);
            normalized.Add(value);
        }
        return normalized.OrderBy(address => address, StringComparer.Ordinal).ToArray();
    }

    private static string WalletGroup(string[] addresses) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', addresses))));

    private async Task<BitcoinTransactionPage> FetchPageAsync(string address, string cursor, CancellationToken cancellationToken)
    {
        var transactions = await _client.GetAddressTransactionsAsync(address, cursor, cancellationToken);
        ValidateHistory(transactions, EsploraClient.ConfirmedPageSize, chainOnly: true);
        if (transactions.Any(tx => string.Equals(tx.TxId, cursor, StringComparison.OrdinalIgnoreCase)))
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        var tip = await _client.GetTipHeightAsync(cancellationToken);
        return new BitcoinTransactionPage
        {
            Transactions = Array.AsReadOnly(transactions.Select(tx => MapTransaction(tx, tip, address)).ToArray()),
            NextCursor = NextCursor(transactions), TipHeight = tip, UpdatedAt = _time.GetUtcNow(),
        };
    }

    private static string? NextCursor(EsploraTransaction[] transactions)
    {
        var confirmed = transactions.Where(tx => tx.Status.Confirmed).ToArray();
        return confirmed.Length == EsploraClient.ConfirmedPageSize ? confirmed[^1].TxId.ToLowerInvariant() : null;
    }

    private static BitcoinTransaction MapTransaction(EsploraTransaction transaction, int tip, string? address = null)
    {
        var status = transaction.Status;
        if (status.Confirmed && status.BlockHeight > tip)
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        var mapped = new BitcoinTransaction
        {
            TxId = transaction.TxId.ToLowerInvariant(), FeeSats = transaction.Fee, Weight = transaction.Weight,
            SizeBytes = transaction.Size, Confirmed = status.Confirmed,
            BlockHeight = status.Confirmed ? status.BlockHeight : null,
            BlockHash = status.Confirmed ? status.BlockHash!.ToLowerInvariant() : null,
            BlockTime = status.Confirmed ? DateTimeOffset.FromUnixTimeSeconds(status.BlockTime!.Value) : null,
            Confirmations = status.Confirmed ? checked(tip - status.BlockHeight!.Value + 1) : 0,
            Inputs = Array.AsReadOnly(transaction.Inputs.Select(input => new BitcoinTransactionInput
            {
                Address = input.PreviousOutput?.Address, ValueSats = input.PreviousOutput?.Value, IsCoinbase = input.IsCoinbase,
            }).ToArray()),
            Outputs = Array.AsReadOnly(transaction.Outputs.Select(output => new BitcoinTransactionOutput
            {
                Address = output.Address, ValueSats = output.Value,
            }).ToArray()),
        };
        return address is null ? mapped : BitcoinWalletAccounting.ApplyContext(mapped, new[] { address });
    }

    private static bool MatchesAddress(string? value, string address) => string.Equals(value, address,
        address.StartsWith("bc1", StringComparison.Ordinal) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidateHistory(EsploraTransaction[] transactions, int maximum, bool chainOnly)
    {
        if (transactions is null || transactions.Length > maximum)
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        foreach (var transaction in transactions)
            ValidateTransaction(transaction);
        if (transactions.Select(tx => tx.TxId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != transactions.Length
            || transactions.Count(tx => tx.Status.Confirmed) > EsploraClient.ConfirmedPageSize
            || transactions.Any(tx => tx.Status.Confirmed != chainOnly))
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
    }

    private static void ValidateTransaction(EsploraTransaction transaction)
    {
        if (transaction is null || !IsHash(transaction.TxId) || transaction.Fee < 0 || transaction.Size <= 0
            || transaction.Weight <= 0 || transaction.Inputs is null || transaction.Outputs is null
            || transaction.Inputs.Length == 0 || transaction.Outputs.Length == 0 || transaction.Status is null
            || transaction.Inputs.Any(input => input is null || input.PreviousOutput?.Value < 0)
            || transaction.Outputs.Any(output => output is null || output.Value < 0))
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        var status = transaction.Status;
        if (status.Confirmed && (status.BlockHeight is null or < 0 || !IsHash(status.BlockHash) || status.BlockTime is null or < 0))
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateAddress(EsploraAddress address, string query)
    {
        if (address is null || !MatchesAddress(address.Address, query) || address.Chain is null || address.Mempool is null
            || address.Chain.FundedSats < 0 || address.Chain.SpentSats < 0
            || address.Chain.SpentSats > address.Chain.FundedSats || address.Chain.TransactionCount < 0
            || address.Mempool.FundedSats < 0 || address.Mempool.SpentSats < 0 || address.Mempool.TransactionCount < 0)
            throw new BitcoinTrackingException(BitcoinTrackingError.Unavailable);
        ValidateCombinedTotals(checked(address.Chain.FundedSats - address.Chain.SpentSats),
            checked(address.Mempool.FundedSats - address.Mempool.SpentSats),
            address.Chain.FundedSats, address.Mempool.FundedSats, address.Chain.SpentSats, address.Mempool.SpentSats);
    }

    private static void ValidateCombinedTotals(long confirmedBalance, long pendingBalance,
        long confirmedReceived, long pendingReceived, long confirmedSpent, long pendingSpent)
    {
        // The UI presents these combined figures. Validate them before a successful snapshot is
        // cached so malformed provider numbers become Unavailable, never a rendering exception.
        _ = checked(confirmedBalance + pendingBalance);
        _ = checked(confirmedReceived + pendingReceived);
        _ = checked(confirmedSpent + pendingSpent);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _walletCursors.Dispose();
    }

    private sealed record CacheEntry(object? Value, BitcoinTrackingError? Error, DateTimeOffset ExpiresAt);
    private sealed record WalletGeneration(string Id, DateTimeOffset ExpiresAt);
    private sealed record WalletAddressPosition(string Address, string? LastSeenTxId, bool HasMore);
    private sealed record WalletAddressHistory(WalletAddressPosition Position, BitcoinTransaction[] Transactions);
    private sealed record WalletCursorState(string Group, string Generation, string[] Addresses,
        WalletAddressPosition[] Positions, string[] SeenTxIds, DateTimeOffset ExpiresAt);
    private sealed record WalletMerge(BitcoinTransaction[] Transactions, WalletAddressPosition[] Positions);
}
