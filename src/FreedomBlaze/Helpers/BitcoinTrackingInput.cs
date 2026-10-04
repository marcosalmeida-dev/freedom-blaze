using System.Security.Cryptography;
using FreedomBlaze.Models.BitcoinTracking;

namespace FreedomBlaze.Helpers;

/// <summary>Parses transaction IDs and checksummed Bitcoin mainnet addresses for read-only tracking.</summary>
public static class BitcoinTrackingInput
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string Bech32Alphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32mChecksum = 0x2bc830a3;
    private static readonly uint[] ChecksumGenerators = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    public static bool TryParse(string? input, out BitcoinTrackingKind kind, out string normalized)
    {
        kind = default;
        normalized = string.Empty;
        ReadOnlySpan<char> value = input.AsSpan().Trim();
        if (value.IsEmpty || value.Length > 90)
            return false;

        if (value.Length == 64 && IsHexadecimal(value))
        {
            kind = BitcoinTrackingKind.Transaction;
            normalized = value.ToString().ToLowerInvariant();
            return true;
        }

        if (value.StartsWith("bc1", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsMainnetWitnessAddress(value))
                return false;

            normalized = value.ToString().ToLowerInvariant();
        }
        else
        {
            if (!IsMainnetBase58Address(value))
                return false;

            // Base58 is case-sensitive; only witness addresses can be lowercased.
            normalized = value.ToString();
        }

        kind = BitcoinTrackingKind.Address;
        return true;
    }

    private static bool IsHexadecimal(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }

        return true;
    }

    private static bool IsMainnetBase58Address(ReadOnlySpan<char> value)
    {
        if (value.Length > 35 || value[0] is not ('1' or '3'))
            return false;

        // Mainnet Base58Check addresses contain one version byte, a 20-byte hash, and four checksum bytes.
        Span<byte> decoded = stackalloc byte[25];
        decoded.Clear();
        int leadingZeros = 0;
        while (leadingZeros < value.Length && value[leadingZeros] == '1')
            leadingZeros++;

        foreach (char character in value)
        {
            int digit = Base58Alphabet.IndexOf(character);
            if (digit < 0)
                return false;

            int carry = digit;
            for (int index = decoded.Length - 1; index >= 0; index--)
            {
                carry += decoded[index] * 58;
                decoded[index] = (byte)carry;
                carry >>= 8;
            }

            if (carry != 0)
                return false;
        }

        int firstNonzero = 0;
        while (firstNonzero < decoded.Length && decoded[firstNonzero] == 0)
            firstNonzero++;

        if (leadingZeros + decoded.Length - firstNonzero != decoded.Length || decoded[0] is not (0 or 5))
            return false;

        Span<byte> firstHash = stackalloc byte[32];
        Span<byte> secondHash = stackalloc byte[32];
        SHA256.HashData(decoded[..21], firstHash);
        SHA256.HashData(firstHash, secondHash);
        return CryptographicOperations.FixedTimeEquals(decoded[21..], secondHash[..4]);
    }

    private static bool IsMainnetWitnessAddress(ReadOnlySpan<char> value)
    {
        // BIP 173 and BIP 350: reject mixed case before normalizing for checksum verification.
        bool hasLowercase = false;
        bool hasUppercase = false;
        foreach (char character in value)
        {
            if (character is < (char)33 or > (char)126)
                return false;

            hasLowercase |= character is >= 'a' and <= 'z';
            hasUppercase |= character is >= 'A' and <= 'Z';
        }

        if (hasLowercase && hasUppercase || value.Length < 14)
            return false;

        Span<byte> data = stackalloc byte[87];
        int dataLength = value.Length - 3;
        uint checksum = 1;
        // Expand the mainnet human-readable part, "bc", per BIP 173.
        checksum = UpdateChecksum(checksum, 'b' >> 5);
        checksum = UpdateChecksum(checksum, 'c' >> 5);
        checksum = UpdateChecksum(checksum, 0);
        checksum = UpdateChecksum(checksum, 'b' & 31);
        checksum = UpdateChecksum(checksum, 'c' & 31);
        for (int index = 0; index < dataLength; index++)
        {
            int digit = Bech32Alphabet.IndexOf(char.ToLowerInvariant(value[index + 3]));
            if (digit < 0)
                return false;

            data[index] = (byte)digit;
            checksum = UpdateChecksum(checksum, digit);
        }

        int version = data[0];
        if (version > 16 || checksum != (version == 0 ? 1u : Bech32mChecksum))
            return false;

        int accumulator = 0;
        int bits = 0;
        int programLength = 0;
        for (int index = 1; index < dataLength - 6; index++)
        {
            accumulator = ((accumulator << 5) | data[index]) & 0xfff;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                programLength++;
            }
        }

        // Unused bits must be zero, with fewer than five padding bits (no extra encoded group).
        if (bits >= 5 || (accumulator & ((1 << bits) - 1)) != 0 || programLength is < 2 or > 40)
            return false;

        return version != 0 || programLength is 20 or 32;
    }

    private static uint UpdateChecksum(uint checksum, int value)
    {
        uint top = checksum >> 25;
        uint next = ((checksum & 0x1ffffff) << 5) ^ (uint)value;
        for (int index = 0; index < ChecksumGenerators.Length; index++)
        {
            if ((top & (1u << index)) != 0)
                next ^= ChecksumGenerators[index];
        }

        return next;
    }
}
