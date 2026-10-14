using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using NBitcoin.Secp256k1;

namespace FreedomBlaze.Services.Lightning;

/// <summary>
/// The parts of a BOLT11 payment request the gateway checks before handing an invoice out: who signed it,
/// which payment it is and for how much. <see cref="TryParse"/> verifies the bech32 checksum and recovers
/// the signer from the signature, so <see cref="PayeeNodeId"/> is the node that actually signed it.
/// </summary>
public sealed class Bolt11Invoice
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const int ChecksumWords = 6;
    private const int TimestampWords = 7;
    private const int SignatureWords = 104; // 520 bits: a 64-byte compact signature and a recovery id
    private const long DefaultExpirySeconds = 3600;

    private static readonly BigInteger CurveOrder =
        new(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"), isUnsigned: true, isBigEndian: true);

    // Half the secp256k1 group order: BOLT11 wants low-S signatures when the payee is named in an n field.
    private static readonly byte[] HalfOrder = Convert.FromHexString("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0");

    private static readonly string[] Currencies = ["bc", "tb", "tbs", "bcrt"];

    private Bolt11Invoice(string currency, long? amountMsat, DateTimeOffset timestamp, string paymentHash,
        string payeeNodeId, long expirySeconds, string? description)
    {
        Currency = currency;
        AmountMsat = amountMsat;
        Timestamp = timestamp;
        PaymentHash = paymentHash;
        PayeeNodeId = payeeNodeId;
        ExpirySeconds = expirySeconds;
        Description = description;
    }

    /// <summary>The network prefix: <c>bc</c> (mainnet), <c>tb</c> (testnet), <c>tbs</c> (signet), <c>bcrt</c> (regtest).</summary>
    public string Currency { get; }

    /// <summary>The amount asked for, in millisatoshis; null for an any-amount invoice.</summary>
    public long? AmountMsat { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>Lower-case hex.</summary>
    public string PaymentHash { get; }

    /// <summary>The compressed public key (lower-case hex) of the node that signed the invoice.</summary>
    public string PayeeNodeId { get; }

    public long ExpirySeconds { get; }

    public string? Description { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out Bolt11Invoice? invoice, [NotNullWhen(false)] out string? error)
    {
        invoice = null;
        var text = value?.Trim() ?? string.Empty;
        var lower = text.ToLowerInvariant();
        if (text != lower && text != text.ToUpperInvariant())
            return Fail("mixed case", out error);
        text = lower;

        var separator = text.LastIndexOf('1');
        if (separator < 2 || text.Length - separator - 1 < TimestampWords + SignatureWords + ChecksumWords)
            return Fail("too short", out error);

        var hrp = text[..separator];
        var words = new byte[text.Length - separator - 1];
        for (var i = 0; i < words.Length; i++)
        {
            var word = Charset.IndexOf(text[separator + 1 + i]);
            if (word < 0)
                return Fail("not bech32", out error);
            words[i] = (byte)word;
        }

        if (!ChecksumIsValid(hrp, words))
            return Fail("bad checksum", out error);

        if (!TryParsePrefix(hrp, out var currency, out var amountMsat, out error))
            return false;

        var data = words.AsSpan(0, words.Length - ChecksumWords);
        var fieldsEnd = data.Length - SignatureWords;

        string? paymentHash = null, description = null;
        byte[]? namedPayee = null;
        var expirySeconds = DefaultExpirySeconds;
        for (var i = TimestampWords; i < fieldsEnd;)
        {
            if (i + 3 > fieldsEnd)
                return Fail("truncated field", out error);

            var type = data[i];
            var length = data[i + 1] * 32 + data[i + 2];
            i += 3;
            if (i + length > fieldsEnd)
                return Fail("truncated field", out error);

            var field = data.Slice(i, length);
            i += length;
            switch (type)
            {
                case 1: // p: payment hash
                    if (length != 52)
                        return Fail("payment hash of the wrong length", out error);
                    paymentHash ??= Convert.ToHexStringLower(ToBytes(field));
                    break;
                case 13: // d: description
                    description ??= Encoding.UTF8.GetString(ToBytes(field));
                    break;
                case 19: // n: payee node id
                    if (length != 53)
                        return Fail("payee of the wrong length", out error);
                    namedPayee ??= ToBytes(field);
                    break;
                case 6: // x: expiry in seconds
                    if (length > 10)
                        return Fail("expiry too long", out error);
                    expirySeconds = ReadNumber(field);
                    break;
                case 16 or 23 when length != 52: // s (payment secret) and h (description hash)
                    return Fail("field of the wrong length", out error);
            }
        }

        if (paymentHash is null)
            return Fail("no payment hash", out error);

        var signature = ToBytes(data[fieldsEnd..]);
        var recoveryId = signature[64];
        if (recoveryId > 3)
            return Fail("bad recovery id", out error);

        if (signature.AsSpan(32, 32).SequenceCompareTo(HalfOrder) > 0)
        {
            if (namedPayee is not null)
                return Fail("high-S signature", out error);

            // Recovery must accept a high-S signature too. BOLT11 treats it as its low-S twin with the same
            // recovery id (the spec's own high-S example only recovers its payee that way), so normalise s.
            var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
            var lowS = (CurveOrder - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            signature.AsSpan(32, 32).Clear();
            lowS.CopyTo(signature.AsSpan(64 - lowS.Length));
        }

        var signed = Encoding.UTF8.GetBytes(hrp).Concat(ToBytes(data[..fieldsEnd], pad: true)).ToArray();
        if (!SecpRecoverableECDSASignature.TryCreateFromCompact(signature.AsSpan(0, 64), recoveryId, out var recoverable)
            || !ECPubKey.TryRecover(Context.Instance, recoverable, System.Security.Cryptography.SHA256.HashData(signed), out var signer))
            return Fail("signature does not recover a key", out error);

        var payee = signer.ToBytes(compressed: true);
        if (namedPayee is not null && !namedPayee.AsSpan().SequenceEqual(payee))
            return Fail("signature is not the named payee's", out error);

        invoice = new Bolt11Invoice(currency, amountMsat, DateTimeOffset.FromUnixTimeSeconds(ReadNumber(data[..TimestampWords])),
            paymentHash, Convert.ToHexStringLower(payee), expirySeconds, description);
        error = null;
        return true;
    }

    /// <summary>Splits <c>ln</c> + currency + optional amount and multiplier.</summary>
    private static bool TryParsePrefix(string hrp, out string currency, out long? amountMsat, [NotNullWhen(false)] out string? error)
    {
        currency = string.Empty;
        amountMsat = null;
        if (!hrp.StartsWith("ln", StringComparison.Ordinal))
            return Fail("not a Lightning invoice", out error);

        var rest = hrp[2..];
        var amountStart = 0;
        while (amountStart < rest.Length && !char.IsAsciiDigit(rest[amountStart]))
            amountStart++;

        currency = rest[..amountStart];
        if (!Currencies.Contains(currency))
            return Fail($"unknown network '{currency}'", out error);

        var amount = rest[amountStart..];
        if (amount.Length == 0)
        {
            error = null;
            return true;
        }

        var multiplier = char.IsAsciiDigit(amount[^1]) ? (char?)null : amount[^1];
        var digits = multiplier is null ? amount : amount[..^1];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)
            || !decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return Fail("bad amount", out error);

        decimal? msat = multiplier switch
        {
            null => number * 100_000_000_000m,
            'm' => number * 100_000_000m,
            'u' => number * 100_000m,
            'n' => number * 100m,
            'p' => number / 10m,
            _ => null,
        };
        if (msat is not { } value)
            return Fail("bad amount multiplier", out error);
        if (value != decimal.Truncate(value))
            return Fail("amount below a millisatoshi", out error);
        if (value > long.MaxValue)
            return Fail("amount too large", out error);

        amountMsat = (long)value;
        error = null;
        return true;
    }

    private static bool ChecksumIsValid(string hrp, ReadOnlySpan<byte> words)
    {
        uint check = 1;
        foreach (var c in hrp)
            check = Polymod(check) ^ (uint)(c >> 5);
        check = Polymod(check);
        foreach (var c in hrp)
            check = Polymod(check) ^ (uint)(c & 31);
        foreach (var word in words)
            check = Polymod(check) ^ word;
        return check == 1;
    }

    private static uint Polymod(uint check)
    {
        var top = check >> 25;
        check = (check & 0x1ffffff) << 5;
        if ((top & 1) != 0) check ^= 0x3b6a57b2;
        if ((top & 2) != 0) check ^= 0x26508e6d;
        if ((top & 4) != 0) check ^= 0x1ea119fa;
        if ((top & 8) != 0) check ^= 0x3d4233dd;
        if ((top & 16) != 0) check ^= 0x2a1462b3;
        return check;
    }

    /// <summary>5-bit words to bytes. Without <paramref name="pad"/>, the trailing bits that do not fill a byte are dropped.</summary>
    private static byte[] ToBytes(ReadOnlySpan<byte> words, bool pad = false)
    {
        var bytes = new List<byte>(words.Length * 5 / 8 + 1);
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

        if (pad && bits > 0)
            bytes.Add((byte)(accumulator << (8 - bits)));
        return [.. bytes];
    }

    private static long ReadNumber(ReadOnlySpan<byte> words)
    {
        long value = 0;
        foreach (var word in words)
            value = value * 32 + word;
        return value;
    }

    private static bool Fail(string reason, out string error)
    {
        error = reason;
        return false;
    }
}
