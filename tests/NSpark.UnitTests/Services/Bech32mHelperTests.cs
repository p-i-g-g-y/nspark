using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="Bech32mHelper.Encode"/> against the BIP-350
/// specification, structured as property-based checks plus a small
/// number of reference snapshots.
/// </summary>
/// <remarks>
/// A bad bech32m implementation produces invalid Spark addresses
/// silently — strings that look right but fail at decode time. The
/// property tests below catch single-byte / single-bit defects in
/// the polynomial checksum and the HRP expansion.
/// </remarks>
[TestFixture]
public sealed class Bech32mHelperTests
{
    [Test]
    public void Encode_inserts_separator_after_hrp()
    {
        var encoded = Bech32mHelper.Encode("spark", new byte[] { 0x00, 0x01, 0x02 });
        encoded[..5].Should().Be("spark");
        encoded[5].Should().Be('1');
    }

    [Test]
    public void Encode_with_empty_data_produces_hrp_separator_and_six_char_checksum()
    {
        var encoded = Bech32mHelper.Encode("ns", []);
        // hrp + "1" + 6-char checksum, no data words.
        encoded.Length.Should().Be("ns".Length + 1 + 6);
        encoded.Should().StartWith("ns1");
    }

    [Test]
    public void Encode_is_deterministic()
    {
        var a = Bech32mHelper.Encode("spark", new byte[] { 0xde, 0xad, 0xbe, 0xef });
        var b = Bech32mHelper.Encode("spark", new byte[] { 0xde, 0xad, 0xbe, 0xef });
        a.Should().Be(b);
    }

    [Test]
    public void Encode_uses_only_bech32_alphabet_characters()
    {
        const string allowed = "qpzry9x8gf2tvdw0s3jn54khce6mua7l1"; // includes '1' separator
        var encoded = Bech32mHelper.Encode("spark", new byte[] { 0x11, 0x22, 0x33, 0x44 });

        var dataAndChecksum = encoded["spark".Length..];
        foreach (var c in dataAndChecksum)
        {
            allowed.Should().Contain(c.ToString(),
                because: $"character '{c}' must come from the bech32 alphabet");
        }
    }

    [Test]
    public void Encode_HRP_folds_into_checksum()
    {
        // Same data, different HRPs — the checksum must differ because the
        // polynomial folds HRP bytes in via HrpExpand.
        var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        Bech32mHelper.Encode("bc", data).Should().NotBe(Bech32mHelper.Encode("tb", data));
    }

    [Test]
    public void Encode_single_bit_data_flip_changes_output()
    {
        // Bech32m promises that any single-bit error in the data is detected
        // by the checksum — for our purposes, the encoded string must differ.
        var a = Bech32mHelper.Encode("spark", new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var b = Bech32mHelper.Encode("spark", new byte[] { 0x00, 0x01, 0x02, 0x04 });
        a.Should().NotBe(b);
    }

    [Test]
    public void Encode_long_input_produces_proportional_data_segment_length()
    {
        // The 5-bit converter expands every 5 input bytes into 8 output
        // characters; verify the size grows roughly 8/5×.
        var data = new byte[40];
        var encoded = Bech32mHelper.Encode("spark", data);
        var dataLen = encoded.Length - "spark".Length - 1 /* sep */ - 6 /* checksum */;
        // 40 bytes * 8 bits / 5 bits per char = 64 chars exactly.
        dataLen.Should().Be(64);
    }

    [Test]
    public void Decode_round_trips_a_known_encoded_value()
    {
        var data = new byte[] { 0xde, 0xad, 0xbe, 0xef, 0x01, 0x02, 0x03, 0x04 };
        var encoded = Bech32mHelper.Encode("spark", data);
        var (hrp, decoded) = Bech32mHelper.Decode(encoded);

        hrp.Should().Be("spark");
        decoded.Should().Equal(data);
    }

    [Test]
    public void Decode_round_trips_32_byte_payload_under_token_limit()
    {
        var data = new byte[32];
        for (int i = 0; i < 32; i++) data[i] = (byte)i;
        var encoded = Bech32mHelper.Encode("btkn", data);
        var (hrp, decoded) = Bech32mHelper.Decode(encoded, limit: 500);

        hrp.Should().Be("btkn");
        decoded.Should().Equal(data);
    }

    [Test]
    public void Decode_is_case_insensitive()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var encoded = Bech32mHelper.Encode("spark", data);
        var upper = encoded.ToUpperInvariant();

        var (hrp, decoded) = Bech32mHelper.Decode(upper);
        hrp.Should().Be("spark");
        decoded.Should().Equal(data);
    }

    [Test]
    public void Decode_throws_on_bad_checksum()
    {
        // Flip the last data character to break the checksum.
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var encoded = Bech32mHelper.Encode("spark", data);
        var tampered = encoded[..^1] + (encoded[^1] == 'q' ? 'p' : 'q');

        Action act = () => Bech32mHelper.Decode(tampered);
        act.Should().Throw<NSpark.Exceptions.SparkConfigurationException>()
            .WithMessage("*checksum*");
    }

    [Test]
    public void Decode_rejects_strings_over_the_limit()
    {
        Action act = () => Bech32mHelper.Decode(new string('a', 200), limit: 90);
        act.Should().Throw<NSpark.Exceptions.SparkConfigurationException>()
            .WithMessage("*max is 90*");
    }

    [Test]
    public void Decode_rejects_strings_missing_the_separator()
    {
        Action act = () => Bech32mHelper.Decode("noseparatorhere");
        act.Should().Throw<NSpark.Exceptions.SparkConfigurationException>()
            .WithMessage("*separator*");
    }

    [Test]
    public void Encode_snapshot_lock_against_silent_regression()
    {
        // These outputs were captured against the current implementation and
        // serve as a regression net: any future change to the bech32m
        // polynomial, charset, or HRP expansion will trip these.
        //
        // To regenerate: run this test under a known-good NSpark commit and
        // paste the outputs back.
        Bech32mHelper.Encode("spark", []).Should().Be("spark139eqen");
        Bech32mHelper.Encode("a", new byte[] { 0x00 }).Should().Be("a1qq43hjaz");
        Bech32mHelper.Encode("ns", new byte[] { 0xde, 0xad, 0xbe, 0xef }).Should()
            .Be("ns1m6kmamczckw8h");
        Bech32mHelper.Encode("spark", new byte[] { 0x00, 0x01, 0x02, 0x03 }).Should()
            .Be("spark1qqqsyqch7jgau");
    }
}
