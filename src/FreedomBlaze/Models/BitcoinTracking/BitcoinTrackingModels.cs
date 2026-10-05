namespace FreedomBlaze.Models.BitcoinTracking;

public enum BitcoinTrackingKind { Transaction, Address, Wallet }

public sealed record BitcoinTrackingResult
{
    public required string Query { get; init; }
    public BitcoinTrackingKind Kind { get; init; }
    public BitcoinTransaction? Transaction { get; init; }
    public BitcoinAddressSummary? Address { get; init; }
    public BitcoinWalletSummary? Wallet { get; init; }
    public IReadOnlyList<string> TrackedAddresses { get; init; } = [];
    public IReadOnlyList<BitcoinTransaction> Transactions { get; init; } = [];
    public string? NextCursor { get; init; }
    public int TipHeight { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record BitcoinTransaction
{
    public required string TxId { get; init; }
    public long FeeSats { get; init; }
    public int Weight { get; init; }
    public int SizeBytes { get; init; }
    public bool Confirmed { get; init; }
    public int? BlockHeight { get; init; }
    public string? BlockHash { get; init; }
    public DateTimeOffset? BlockTime { get; init; }
    public int Confirmations { get; init; }
    public IReadOnlyList<BitcoinTransactionInput> Inputs { get; init; } = [];
    public IReadOnlyList<BitcoinTransactionOutput> Outputs { get; init; } = [];
    /// <summary>Outputs to the searched address minus its spent inputs; not a wallet-wide payment amount.</summary>
    public long? AddressNetSats { get; init; }
    public long? TrackedReceivedSats { get; init; }
    public long? TrackedSpentSats { get; init; }
    public long? TrackedNetSats { get; init; }
    public long? IncomingSats => TrackedNetSats is { } net ? Math.Max(net, 0) : null;
    /// <summary>Net value leaving the tracked addresses, including any transaction fees they pay.</summary>
    public long? OutgoingSats => TrackedNetSats is { } net ? Math.Max(-net, 0) : null;
}

public sealed record BitcoinTransactionInput
{
    public string? Address { get; init; }
    public long? ValueSats { get; init; }
    public bool IsCoinbase { get; init; }
}

public sealed record BitcoinTransactionOutput
{
    public string? Address { get; init; }
    public long ValueSats { get; init; }
}

public sealed record BitcoinAddressSummary
{
    public long ConfirmedBalanceSats { get; init; }
    public long PendingBalanceChangeSats { get; init; }
    public long ConfirmedReceivedSats { get; init; }
    public long ConfirmedSpentSats { get; init; }
    public long PendingReceivedSats { get; init; }
    public long PendingSpentSats { get; init; }
    public long TotalBalanceSats => checked(ConfirmedBalanceSats + PendingBalanceChangeSats);
    public int ConfirmedTransactionCount { get; init; }
    public int PendingTransactionCount { get; init; }
}

/// <summary>Lifetime gross sums from complete provider address statistics, including internal transfers and change.</summary>
public sealed record BitcoinWalletSummary
{
    public int AddressCount { get; init; }
    public long ConfirmedBalanceSats { get; init; }
    public long PendingBalanceChangeSats { get; init; }
    public long ConfirmedReceivedSats { get; init; }
    public long ConfirmedSpentSats { get; init; }
    public long PendingReceivedSats { get; init; }
    public long PendingSpentSats { get; init; }
    public long TotalBalanceSats => checked(ConfirmedBalanceSats + PendingBalanceChangeSats);
}

public sealed record BitcoinTransactionPage
{
    public IReadOnlyList<BitcoinTransaction> Transactions { get; init; } = [];
    public string? NextCursor { get; init; }
    public int TipHeight { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
