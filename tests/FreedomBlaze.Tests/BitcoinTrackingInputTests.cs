using FreedomBlaze.Helpers;
using FreedomBlaze.Models.BitcoinTracking;

namespace FreedomBlaze.Tests;

public sealed class BitcoinTrackingInputTests
{
    // Published BIP 173 / BIP 350 mainnet vectors cover v0, Taproot, future versions,
    // and the minimum/maximum permitted witness program lengths.
    // https://github.com/bitcoin/bips/blob/master/bip-0173.mediawiki#test-vectors
    // https://github.com/bitcoin/bips/blob/master/bip-0350.mediawiki#test-vectors
    [Theory]
    [InlineData("BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4")]
    [InlineData("bc1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3qccfmv3")]
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0")]
    [InlineData("bc1pw508d6qejxtdg4y5r3zarvary0c5xw7kw508d6qejxtdg4y5r3zarvary0c5xw7kt5nd6y")]
    [InlineData("BC1SW50QGDZ25J")]
    [InlineData("bc1zw508d6qejxtdg4y5r3zarvaryvaxxpcs")]
    public void AcceptsPublishedMainnetWitnessVectorsAndNormalizesCase(string address)
    {
        Assert.True(BitcoinTrackingInput.TryParse($" \t{address}\r\n", out var kind, out var normalized));
        Assert.Equal(BitcoinTrackingKind.Address, kind);
        Assert.Equal(address.ToLowerInvariant(), normalized);

        Assert.True(BitcoinTrackingInput.TryParse(address.ToUpperInvariant(), out kind, out normalized));
        Assert.Equal(BitcoinTrackingKind.Address, kind);
        Assert.Equal(address.ToLowerInvariant(), normalized);
    }

    // Mainnet P2PKH/P2SH vectors from Bitcoin Core's key I/O test fixtures.
    // https://github.com/bitcoin/bitcoin/blob/master/src/test/data/key_io_valid.json
    [Theory]
    [InlineData("1FsSia9rv4NeEwvJ2GvXrX7LyxYspbN2mo")]
    [InlineData("36j4NfKv6Akva9amjWrLG6MuSQym1GuEmm")]
    [InlineData("1FjL87pn8ky6Vbavd1ZHeChRXtoxwRGCRd")]
    [InlineData("3BZECeAH8gSKkjrTx8PwMrNQBLG18yHpvf")]
    [InlineData("1111111111111111111114oLvT2")]
    public void AcceptsMainnetBase58CheckAddressesWithoutChangingCase(string address)
    {
        Assert.True(BitcoinTrackingInput.TryParse($" \t{address}\r\n", out var kind, out var normalized));
        Assert.Equal(BitcoinTrackingKind.Address, kind);
        Assert.Equal(address, normalized);
    }

    [Fact]
    public void AcceptsAnUppercaseTransactionIdAndTrimsWhitespace()
    {
        const string txid = "00000000ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01";

        Assert.True(BitcoinTrackingInput.TryParse($"\r\n\t {txid} \t", out var kind, out var normalized));
        Assert.Equal(BitcoinTrackingKind.Transaction, kind);
        Assert.Equal(txid.ToLowerInvariant(), normalized);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(91)]
    [InlineData(10000)]
    public void RejectsIncorrectTransactionLengthsAndOversizedInput(int length)
    {
        AssertRejected(new string('a', length));
    }

    [Fact]
    public void RejectsNonHexCharactersInA64CharacterTransactionId()
    {
        AssertRejected(new string('a', 63) + "g");
        AssertRejected(new string('a', 63) + "\uff21");
        AssertRejected(new string('a', 31) + " " + new string('a', 32));
    }

    // Valid alternate-network addresses (and a private key) from Bitcoin Core and BIP 350.
    [Theory]
    [InlineData("mzK2FFDEhxqHcmrJw1ysqFkVyhUULo45hZ")]
    [InlineData("2NC2hEhe28ULKAJkW5MjZ3jtTMJdvXmByvK")]
    [InlineData("mww4LvqtTMKvmeQvizPz2EQv26xTneWrbg")]
    [InlineData("2N1r7aC69VHeE7yQJPDLi9T1PYq4wnwvjuT")]
    [InlineData("n4fajahJrAuKbN7uNsKjLjQkz9Qn5ewJXQ")]
    [InlineData("tb1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3q0sl5k7")]
    [InlineData("tb1pqqqqp399et2xygdj5xreqhjjvcmzhxw4aywxecjdzew6hylgvsesf3hn0c")]
    [InlineData("bcrt1qdavt4j2sd7dlhqsavtnfxvzppw6k7qy97tmnu9")]
    [InlineData("5JuW2AMDYu4xVwRG9DZW18VbzQrGcd5RCgb99sS6ehJsNQXu5b9")]
    public void RejectsOtherNetworksAndPrivateKeys(string input)
    {
        AssertRejected(input);
    }

    // Published invalid mainnet vectors exercise checksum variants, witness versions/lengths,
    // zero padding, and mixed case. Valid generic Bech32 strings are not Bitcoin addresses.
    [Theory]
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5")]
    [InlineData("tc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vq5zuyut")]
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqh2y7hd")]
    [InlineData("BC1S0XLXVLHEMJA6C4DQV22UAPCTQUPFHLXM9H8Z3K2E72Q4K9HCZ7VQ54WELL")]
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kemeawh")]
    [InlineData("bc1p38j9r5y49hruaue7wxjce0updqjuyyx0kh56v8s25huc6995vvpql3jow4")]
    [InlineData("BC130XLXVLHEMJA6C4DQV22UAPCTQUPFHLXM9H8Z3K2E72Q4K9HCZ7VQ7ZWS8R")]
    [InlineData("bc1pw5dgrnzv")]
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7v8n0nx0muaewav253zgeav")]
    [InlineData("BC1QR508D6QEJXTDG4Y5R3ZARVARYV98GJ9P")]
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7v07qwwzcrf")]
    [InlineData("bc1zw508d6qejxtdg4y5r3zarvaryvqyzf3du")]
    [InlineData("bc1gmk9yu")]
    [InlineData("bc1Qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4")]
    [InlineData("a12uel5l")]
    [InlineData("a1lqfn3a")]
    public void RejectsInvalidWitnessAddresses(string input)
    {
        AssertRejected(input);
    }

    [Fact]
    public void RejectsNonzeroWitnessPaddingWithAnOtherwiseValidMainnetChecksum()
    {
        // BIP 350's nonzero-padding testnet vector, re-encoded with the mainnet HRP and checksum.
        AssertRejected("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vplqq80a");
    }

    [Theory]
    [InlineData("1FsSia9rv4NeEwvJ2GvXrX7LyxYspbN2mo")]
    [InlineData("36j4NfKv6Akva9amjWrLG6MuSQym1GuEmm")]
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4")]
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0")]
    public void RejectsSingleCharacterChecksumCorruption(string address)
    {
        char replacement = address[^1] == 'q' ? 'p' : 'q';
        AssertRejected(address[..^1] + replacement);
    }

    // Independently encoded Base58Check fixtures have valid checksums but 19/21-byte payload
    // hashes or an unsupported version. A checksum by itself is insufficient validation.
    [Theory]
    [InlineData("12D2adLM3UKy4Z4giRbReR6gjWx1w6Dz")]
    [InlineData("1QXEx2ZQ9mEdvMSaVKHznFv6iZq2LQbDz8")]
    [InlineData("TTazDDREDxxh1mPyGySut6H98h4UKPG6")]
    [InlineData("9tT9KH26AxgN8j9uTpKdwUkK6LFcSKp4FpF")]
    [InlineData("omY4C1wopqNMJxhK8WBv2gwMPAgfqvjmC")]
    [InlineData("11FsSia9rv4NeEwvJ2GvXrX7LyxYspbN2mo")]
    public void RejectsIncorrectBase58PayloadsOrExtraLeadingZeros(string input)
    {
        AssertRejected(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("satoshi")]
    [InlineData("lnbc10u1pjaxexample")]
    [InlineData("user@example.com")]
    [InlineData("bitcoin:1FsSia9rv4NeEwvJ2GvXrX7LyxYspbN2mo")]
    [InlineData("1FsSia9rv4NeEwvJ2GvXrX7LyxYspbN2m0")]
    public void RejectsUnrelatedOrIncompleteInputs(string? input)
    {
        AssertRejected(input);
    }

    private static void AssertRejected(string? input)
    {
        Assert.False(BitcoinTrackingInput.TryParse(input, out _, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }
}
