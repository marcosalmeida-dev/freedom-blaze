namespace FreedomBlaze.Models.BitcoinTracking;

public enum BitcoinTrackingKind { Transaction, Address }

public sealed record BitcoinTrackingResult
{
    public required string Query { get; init; }
    public BitcoinTrackingKind Kind { get; init; }
    public BitcoinTransaction? Transaction { get; init; }
    public BitcoinAddressSummary? Address { get; init; }
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
    public int ConfirmedTransactionCount { get; init; }
    public int PendingTransactionCount { get; init; }
}

public sealed record BitcoinTransactionPage
{
    public IReadOnlyList<BitcoinTransaction> Transactions { get; init; } = [];
    public string? NextCursor { get; init; }
    public int TipHeight { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
