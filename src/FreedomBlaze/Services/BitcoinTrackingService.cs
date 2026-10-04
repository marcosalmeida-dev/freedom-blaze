using System.Collections.Concurrent;
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
    private readonly EsploraClient _client;
    private readonly BitcoinTrackingOptions _options;
    private readonly TimeProvider _time;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _inFlight = new(StringComparer.Ordinal);

    public BitcoinTrackingService(EsploraClient client, IOptions<BitcoinTrackingOptions> options, TimeProvider time)
    {
        _client = client;
        _options = options.Value;
        _time = time;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxCacheEntries });
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

    private async Task<T> GetSharedAsync<T>(string key, Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken) where T : class
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
                () => FetchAndCacheAsync(key, fetch), LazyThreadSafetyMode.ExecutionAndPublication));
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

    private async Task<CacheEntry> FetchAndCacheAsync<T>(string key, Func<CancellationToken, Task<T>> fetch)
        where T : class
    {
        try
        {
            if (TryReadCache(key, out var cached))
                return cached!;
            using var timeout = new CancellationTokenSource(_options.RequestTimeout, _time);
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
                ConfirmedTransactionCount = address.Chain.TransactionCount,
                PendingTransactionCount = address.Mempool.TransactionCount,
            },
            // Each list response carries fresh current-chain statuses. They are only retained for
            // CacheDuration, so refreshed pages can lower confirmations or return a tx to the mempool.
            Transactions = Array.AsReadOnly(transactions.Select(tx => MapTransaction(tx, height, query)).ToArray()),
            NextCursor = NextCursor(confirmed),
            TipHeight = height,
            UpdatedAt = _time.GetUtcNow(),
        };
    }

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
        long? net = null;
        if (address is not null && transaction.Inputs.All(input => input.IsCoinbase || input.PreviousOutput is not null))
        {
            var received = transaction.Outputs.Where(output => MatchesAddress(output.Address, address)).Sum(output => output.Value);
            var spent = transaction.Inputs.Where(input => MatchesAddress(input.PreviousOutput?.Address, address))
                .Sum(input => input.PreviousOutput!.Value);
            net = checked(received - spent);
        }
        return new BitcoinTransaction
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
            AddressNetSats = net,
        };
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
    }

    public void Dispose() => _cache.Dispose();

    private sealed record CacheEntry(object? Value, BitcoinTrackingError? Error, DateTimeOffset ExpiresAt);
}
