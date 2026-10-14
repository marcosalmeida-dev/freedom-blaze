using System.Text;
using NBitcoin.Secp256k1;

namespace FreedomBlaze.Tests;

/// <summary>Writes signed BOLT11 invoices, so a test can play a node: an honest one or not.</summary>
internal static class Bolt11TestInvoices
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>The key BOLT11's own examples are signed with; its node id is <see cref="SpecNodeId"/>.</summary>
    public static readonly ECPrivKey SpecKey = Key("e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734");

    public const string SpecNodeId = "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad";

    public static ECPrivKey Key(string hex) => Context.Instance.CreateECPrivKey(Convert.FromHexString(hex));

    public static ECPrivKey NewKey() =>
        Context.Instance.CreateECPrivKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static string NodeId(ECPrivKey key) => Convert.ToHexStringLower(key.CreatePubKey().ToBytes(true));

    public static string Create(ECPrivKey signer, string paymentHash, long? amountMsat, string description,
        long expirySeconds, DateTimeOffset timestamp, string currency = "bc")
    {
        var hrp = "ln" + currency + Amount(amountMsat);
        var words = new List<byte>(Number(timestamp.ToUnixTimeSeconds(), 7));
        AddField(words, 1, ToWords(Convert.FromHexString(paymentHash)));
        AddField(words, 13, ToWords(Encoding.UTF8.GetBytes(description)));
        AddField(words, 6, Number(expirySeconds));

        var signed = Encoding.UTF8.GetBytes(hrp).Concat(ToBytes(words)).ToArray();
        if (!signer.TrySignRecoverable(System.Security.Cryptography.SHA256.HashData(signed), out var signature) || signature is null)
            throw new InvalidOperationException("Could not sign the test invoice.");

        var compact = new byte[65];
        signature.WriteToSpanCompact(compact.AsSpan(0, 64), out var recoveryId);
        compact[64] = (byte)recoveryId;
        words.AddRange(ToWords(compact));

        return hrp + "1" + new string([.. words.Concat(Checksum(hrp, words)).Select(word => Charset[word])]);
    }

    private static string Amount(long? msat) => msat switch
    {
        null => string.Empty,
        { } value when value % 100_000 == 0 => $"{value / 100_000}u",
        { } value when value % 100 == 0 => $"{value / 100}n",
        { } value => $"{value * 10}p",
    };

    private static void AddField(List<byte> words, byte type, IReadOnlyList<byte> data)
    {
        words.Add(type);
        words.Add((byte)(data.Count / 32));
        words.Add((byte)(data.Count % 32));
        words.AddRange(data);
    }

    private static byte[] Number(long value, int length)
    {
        var words = new byte[length];
        for (var i = length - 1; i >= 0; i--, value /= 32)
            words[i] = (byte)(value % 32);
        return words;
    }

    private static byte[] Number(long value)
    {
        var length = 1;
        while (value >> (5 * length) > 0)
            length++;
        return Number(value, length);
    }

    private static byte[] ToWords(byte[] bytes)
    {
        var words = new List<byte>();
        int accumulator = 0, bits = 0;
        foreach (var b in bytes)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                words.Add((byte)((accumulator >> bits) & 31));
            }
        }

        if (bits > 0)
            words.Add((byte)((accumulator << (5 - bits)) & 31));
        return [.. words];
    }

    private static byte[] ToBytes(IEnumerable<byte> words)
    {
        var bytes = new List<byte>();
        int accumulator = 0, bits = 0;
        foreach (var word in words)
        {
            accumulator = (accumulator << 5) | word;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(accumulator >> bits));
                accumulator &= (1 << bits) - 1;
            }
        }

        if (bits > 0)
            bytes.Add((byte)(accumulator << (8 - bits)));
        return [.. bytes];
    }

    private static byte[] Checksum(string hrp, IEnumerable<byte> words)
    {
        var values = hrp.Select(c => (byte)(c >> 5)).Append((byte)0).Concat(hrp.Select(c => (byte)(c & 31)))
            .Concat(words).Concat(new byte[6]);
        uint check = 1;
        foreach (var value in values)
        {
            var top = check >> 25;
            check = ((check & 0x1ffffff) << 5) ^ value;
            if ((top & 1) != 0) check ^= 0x3b6a57b2;
            if ((top & 2) != 0) check ^= 0x26508e6d;
            if ((top & 4) != 0) check ^= 0x1ea119fa;
            if ((top & 8) != 0) check ^= 0x3d4233dd;
            if ((top & 16) != 0) check ^= 0x2a1462b3;
        }

        check ^= 1;
        return [.. Enumerable.Range(0, 6).Select(i => (byte)((check >> (5 * (5 - i))) & 31))];
    }
}
