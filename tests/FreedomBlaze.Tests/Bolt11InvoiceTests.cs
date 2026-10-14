using FreedomBlaze.Services.Lightning;

namespace FreedomBlaze.Tests;

/// <summary>The examples from BOLT #11 itself, signed by the spec's key, plus its invalid ones.</summary>
public sealed class Bolt11InvoiceTests
{
    private const string SpecHash = "0001020304050607080900010203040506070809000102030405060708090102";

    private const string Donation =
        "lnbc1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq9qrsgq357wnc5r2ueh7ck6q93dj32dlqnls087fxdwk8qakdyafkq3yap9us6v52vjjsrvywa6rt52cm9r9zqt8r2t7mlcwspyetp5h2tztugp9lfyql";

    private const string Coffee =
        "lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgpfna3rh";

    private const string PicoAmount =
        "lnbc9678785340p1pwmna7lpp5gc3xfm08u9qy06djf8dfflhugl6p7lgza6dsjxq454gxhj9t7a0sd8dgfkx7cmtwd68yetpd5s9xar0wfjn5gpc8qhrsdfq24f5ggrxdaezqsnvda3kkum5wfjkzmfqf3jkgem9wgsyuctwdus9xgrcyqcjcgpzgfskx6eqf9hzqnteypzxz7fzypfhg6trddjhygrcyqezcgpzfysywmm5ypxxjemgw3hxjmn8yptk7untd9hxwg3q2d6xjcmtv4ezq7pqxgsxzmnyyqcjqmt0wfjjq6t5v4khxsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygsxqyjw5qcqp2rzjq0gxwkzc8w6323m55m4jyxcjwmy7stt9hwkwe2qxmy8zpsgg7jcuwz87fcqqeuqqqyqqqqlgqqqqn3qq9q9qrsgqrvgkpnmps664wgkp43l22qsgdw4ve24aca4nymnxddlnp8vh9v2sdxlu5ywdxefsfvm0fq3sesf08uf6q9a2ke0hc9j6z6wlxg5z5kqpu2v9wz";

    private const string Testnet =
        "lntb20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfpp3x9et2e20v6pu37c5d9vax37wxq72un989qrsgqdj545axuxtnfemtpwkc45hx9d2ft7x04mt8q7y6t0k2dge9e7h8kpy9p34ytyslj3yu569aalz2xdk8xkd7ltxqld94u8h2esmsmacgpghe9k8";

    private const string HighS =
        "lnbc1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq9qrsgq357wnc5r2ueh7ck6q93dj32dlqnls087fxdwk8qakdyafkq3yap2r09nt4ndd0unm3z9u5t48y6ucv4r5sg7lk98c77ctvjczkspk5qprc90gx";

    private const string UpperCase =
        "LNBC25M1PVJLUEZPP5QQQSYQCYQ5RQWZQFQQQSYQCYQ5RQWZQFQQQSYQCYQ5RQWZQFQYPQDQ5VDHKVEN9V5SXYETPDEESSP5ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYGS9Q5SQQQQQQQQQQQQQQQQSGQ2A25DXL5HRNTDTN6ZVYDT7D66HYZSYHQS4WDYNAVYS42XGL6SGX9C4G7ME86A27T07MDTFRY458RTJR0V92CNMSWPSJSCGT2VCSE3SGPZ3UAPA";

    [Fact]
    public void AnAnyAmountDonationNamesItsSignerAndPayment()
    {
        Assert.True(Bolt11Invoice.TryParse(Donation, out var invoice, out var error), error);

        Assert.Equal("bc", invoice.Currency);
        Assert.Null(invoice.AmountMsat);
        Assert.Equal(SpecHash, invoice.PaymentHash);
        Assert.Equal(Bolt11TestInvoices.SpecNodeId, invoice.PayeeNodeId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1496314658), invoice.Timestamp);
        Assert.Equal(3600, invoice.ExpirySeconds);
        Assert.Equal("Please consider supporting this project", invoice.Description);
    }

    [Theory]
    [InlineData(Coffee, "bc", 250_000_000L, 60L)]
    [InlineData(PicoAmount, "bc", 967_878_534L, 604_800L)]
    [InlineData(Testnet, "tb", 2_000_000_000L, 3600L)]
    [InlineData(UpperCase, "bc", 2_500_000_000L, 3600L)]
    public void AmountsNetworksAndExpiriesAreRead(string value, string currency, long amountMsat, long expirySeconds)
    {
        Assert.True(Bolt11Invoice.TryParse(value, out var invoice, out var error), error);

        Assert.Equal(currency, invoice.Currency);
        Assert.Equal(amountMsat, invoice.AmountMsat);
        Assert.Equal(expirySeconds, invoice.ExpirySeconds);
        Assert.Equal(Bolt11TestInvoices.SpecNodeId, invoice.PayeeNodeId);
    }

    [Fact]
    public void TheSignerIsRecoveredEvenFromAHighSSignature()
    {
        Assert.True(Bolt11Invoice.TryParse(HighS, out var invoice, out var error), error);

        Assert.Equal(Bolt11TestInvoices.SpecNodeId, invoice.PayeeNodeId);
    }

    [Theory]
    [InlineData("lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpquwpc4curk03c9wlrswe78q4eyqc7d8d0xqzpuyk0sg5g70me25alkluzd2x62aysf2pyy8edtjeevuv4p2d5p76r4zkmneet7uvyakky2zr4cusd45tftc9c5fh0nnqpnl2jfll544esqchsrnt", "bad checksum")]
    [InlineData("pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpquwpc4curk03c9wlrswe78q4eyqc7d8d0xqzpuyk0sg5g70me25alkluzd2x62aysf2pyy8edtjeevuv4p2d5p76r4zkmneet7uvyakky2zr4cusd45tftc9c5fh0nnqpnl2jfll544esqchsrnt", "too short")]
    [InlineData("LNBC2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpudq5xysxxatsyp3k7enxv4jsxqzpu", "mixed case")]
    [InlineData("lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6na6hlh", "too short")]
    [InlineData("lnbc2500x1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpusp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9qrsgqrrzc4cvfue4zp3hggxp47ag7xnrlr8vgcmkjxk3j5jqethnumgkpqp23z9jclu3v0a7e0aruz366e9wqdykw6dxhdzcjjhldxq0w6wgqcnu43j", "bad amount multiplier")]
    [InlineData("lnbc2500000001p1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpusp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9qrsgq0lzc236j96a95uv0m3umg28gclm5lqxtqqwk32uuk4k6673k6n5kfvx3d2h8s295fad45fdhmusm8sjudfhlf6dcsxmfvkeywmjdkxcp99202x", "amount below a millisatoshi")]
    [InlineData("lnbc25m1p70xwfzpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaqnp4q0n326hr8v9zprg8gsvezcch06gfaqqhde2aj730yg0durunfhv66sp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9qrsgqsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygsp5cfzp9ugllvk03rltd6hvndxj26ux6gcxc5azyxk060rj9tzghct5zvjlps76gx8wpq5yuu79688k8gnm2c0al6v608s96l0xzrrlqqwnzxmu", "high-S signature")]
    [InlineData("", "too short")]
    public void TheSpecsInvalidInvoicesAreRefused(string value, string reason)
    {
        Assert.False(Bolt11Invoice.TryParse(value, out var invoice, out var error));

        Assert.Null(invoice);
        Assert.Equal(reason, error);
    }

    [Fact]
    public void AnInvoiceWrittenByTheTestNodeReadsBack()
    {
        var key = Bolt11TestInvoices.NewKey();
        var hash = new string('7', 64);
        var issuedAt = new DateTimeOffset(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

        var bolt11 = Bolt11TestInvoices.Create(key, hash, 10_000_000, "Artcana pedido 123", 600, issuedAt);

        Assert.True(Bolt11Invoice.TryParse(bolt11, out var invoice, out var error), error);
        Assert.Equal(Bolt11TestInvoices.NodeId(key), invoice.PayeeNodeId);
        Assert.Equal(hash, invoice.PaymentHash);
        Assert.Equal(10_000_000, invoice.AmountMsat);
        Assert.Equal(600, invoice.ExpirySeconds);
        Assert.Equal(issuedAt, invoice.Timestamp);
        Assert.Equal("Artcana pedido 123", invoice.Description);
    }

    [Fact]
    public void ChangingAnyCharacterBreaksTheInvoice()
    {
        var bolt11 = Bolt11TestInvoices.Create(Bolt11TestInvoices.NewKey(), new string('7', 64), 10_000_000, "x", 600, DateTimeOffset.UnixEpoch);
        var tampered = bolt11[..20] + (bolt11[20] == 'q' ? 'p' : 'q') + bolt11[21..];

        Assert.False(Bolt11Invoice.TryParse(tampered, out _, out var error));
        Assert.Equal("bad checksum", error);
    }
}
