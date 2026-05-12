using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the BOLT11 invoice decoder embedded in <see cref="LightningService"/>.
/// </summary>
/// <remarks>
/// The decoder extracts two pieces of information from BOLT11:
/// <list type="bullet">
///   <item><description>the payment hash (32-byte tagged field 1)</description></item>
///   <item><description>the amount (HRP suffix with optional milli/micro/nano/pico multiplier)</description></item>
/// </list>
/// A bug in either path can route payments incorrectly or settle for the
/// wrong amount, so these tests double up as a regression net against
/// silent misparses.
/// </remarks>
[TestFixture]
public sealed class Bolt11DecoderTests
{
    // The canonical BOLT11 test invoice from the spec — no amount, payment
    // hash 0001020304050607080900010203040506070809000102030405060708090102.
    // https://github.com/lightning/bolts/blob/master/11-payment-encoding.md
    private const string SpecInvoiceNoAmount =
        "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    [Test]
    public void GetPaymentHash_extracts_known_32_byte_payment_hash_from_spec_invoice()
    {
        var expected = Convert.FromHexString("0001020304050607080900010203040506070809000102030405060708090102");
        var actual = LightningService.Bolt11Decoder.GetPaymentHash(SpecInvoiceNoAmount);
        actual.Should().Equal(expected);
    }

    [Test]
    public void GetPaymentHash_returns_exactly_32_bytes()
    {
        var hash = LightningService.Bolt11Decoder.GetPaymentHash(SpecInvoiceNoAmount);
        hash.Length.Should().Be(32);
    }

    [TestCase("lnbc25m1pvjluez", 2_500_000L, TestName = "25 milli BTC = 2,500,000 sats")]
    [TestCase("lnbc20m1pvjluez", 2_000_000L, TestName = "20 milli BTC = 2,000,000 sats")]
    [TestCase("lnbc1500n1pvjluez", 150L, TestName = "1500 nano BTC = 150 sats")]
    [TestCase("lnbc2500u1pvjluez", 250_000L, TestName = "2500 micro BTC = 250,000 sats")]
    [TestCase("lnbc1m1pvjluez", 100_000L, TestName = "1 milli BTC = 100,000 sats")]
    public void GetAmountSats_parses_BOLT11_multiplier_correctly(string invoiceHrp, long expectedSats)
    {
        // The decoder only inspects the HRP prefix and the bech32 separator
        // position, so it doesn't matter what follows the separator for
        // amount extraction. We append a short bech32-safe payload + the
        // mandatory 6-char checksum padding to keep the parser happy.
        var sythetic = invoiceHrp + "qqqqqq"; // 6-char "data" stand-in
        LightningService.Bolt11Decoder.GetAmountSats(sythetic).Should().Be(expectedSats);
    }

    [TestCase("lnbc1pvjluez")]   // no amount on mainnet
    [TestCase("lntb1pvjluez")]   // no amount on testnet
    [TestCase("lnbcrt1pvjluez")] // no amount on regtest
    public void GetAmountSats_throws_when_invoice_has_no_amount(string invoice)
    {
        Action act = () => LightningService.Bolt11Decoder.GetAmountSats(invoice);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no amount*");
    }

    [Test]
    public void GetAmountSats_throws_on_unknown_network_prefix()
    {
        // "lnxx" is not a recognized Lightning HRP.
        Action act = () => LightningService.Bolt11Decoder.GetAmountSats("lnxx25m1pvjluez");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown BOLT11 network prefix*");
    }

    [Test]
    public void GetAmountSats_throws_on_unknown_multiplier_character()
    {
        // BOLT11 multipliers are m, u, n, p only.
        Action act = () => LightningService.Bolt11Decoder.GetAmountSats("lnbc25z1pvjluez");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown BOLT11 multiplier*");
    }

    [Test]
    public void GetAmountSats_handles_uppercase_invoices_via_case_folding()
    {
        // BOLT11 invoices are case-insensitive; the decoder lowercases internally.
        var lower = "lnbc25m1qqqqqq";
        var upper = lower.ToUpperInvariant();
        LightningService.Bolt11Decoder.GetAmountSats(lower).Should()
            .Be(LightningService.Bolt11Decoder.GetAmountSats(upper));
    }
}
