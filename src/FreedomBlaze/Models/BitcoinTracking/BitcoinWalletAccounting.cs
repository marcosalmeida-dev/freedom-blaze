namespace FreedomBlaze.Models.BitcoinTracking;

public static class BitcoinWalletAccounting
{
    /// <summary>
    /// Applies an explicit watch-only address set. Outputs returned as change or transferred between
    /// tracked addresses cancel spent inputs in the net amount. Missing previous outputs leave the
    /// spent/net amounts unknown. This does not infer ownership of unlisted addresses.
    /// </summary>
    public static BitcoinTransaction ApplyContext(BitcoinTransaction transaction, IReadOnlyCollection<string> addresses)
    {
        var tracked = addresses.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (tracked.Count == 0)
            return transaction with { TrackedReceivedSats = null, TrackedSpentSats = null, TrackedNetSats = null, AddressNetSats = null };
        var received = transaction.Outputs.Where(output => output.Address is { } address && tracked.Contains(Normalize(address)))
            .Sum(output => output.ValueSats);
        long? spent = transaction.Inputs.All(input => input.IsCoinbase || input.ValueSats is not null)
            ? transaction.Inputs.Where(input => !input.IsCoinbase && input.Address is { } address && tracked.Contains(Normalize(address)))
                .Sum(input => input.ValueSats!.Value)
            : null;
        var net = spent is { } value ? checked(received - value) : (long?)null;
        return transaction with
        {
            TrackedReceivedSats = received, TrackedSpentSats = spent, TrackedNetSats = net,
            AddressNetSats = tracked.Count == 1 ? net : null,
        };
    }

    private static string Normalize(string address) => address.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)
        ? address.ToLowerInvariant() : address;
}
