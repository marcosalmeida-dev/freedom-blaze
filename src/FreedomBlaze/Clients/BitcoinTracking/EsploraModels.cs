using System.Text.Json.Serialization;

namespace FreedomBlaze.Clients.BitcoinTracking;

public sealed record EsploraTransaction
{
    [JsonPropertyName("txid")] public required string TxId { get; init; }
    [JsonPropertyName("fee")] public required long Fee { get; init; }
    [JsonPropertyName("size")] public required int Size { get; init; }
    [JsonPropertyName("weight")] public required int Weight { get; init; }
    [JsonPropertyName("vin")] public required EsploraInput[] Inputs { get; init; }
    [JsonPropertyName("vout")] public required EsploraOutput[] Outputs { get; init; }
    [JsonPropertyName("status")] public required EsploraStatus Status { get; init; }
}

public sealed record EsploraInput
{
    [JsonPropertyName("is_coinbase")] public required bool IsCoinbase { get; init; }
    [JsonPropertyName("prevout")] public EsploraOutput? PreviousOutput { get; init; }
}

public sealed record EsploraOutput
{
    [JsonPropertyName("scriptpubkey_address")] public string? Address { get; init; }
    [JsonPropertyName("value")] public required long Value { get; init; }
}

public sealed record EsploraStatus
{
    [JsonPropertyName("confirmed")] public required bool Confirmed { get; init; }
    [JsonPropertyName("block_height")] public int? BlockHeight { get; init; }
    [JsonPropertyName("block_hash")] public string? BlockHash { get; init; }
    [JsonPropertyName("block_time")] public long? BlockTime { get; init; }
}

public sealed record EsploraAddress
{
    [JsonPropertyName("address")] public required string Address { get; init; }
    [JsonPropertyName("chain_stats")] public required EsploraAddressStats Chain { get; init; }
    [JsonPropertyName("mempool_stats")] public required EsploraAddressStats Mempool { get; init; }
}

public sealed record EsploraAddressStats
{
    [JsonPropertyName("funded_txo_sum")] public required long FundedSats { get; init; }
    [JsonPropertyName("spent_txo_sum")] public required long SpentSats { get; init; }
    [JsonPropertyName("tx_count")] public required int TransactionCount { get; init; }
}
