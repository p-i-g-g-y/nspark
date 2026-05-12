using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the 16-byte big-endian UInt128 codec used in token amounts.
/// Ported from <c>Tests/SparkSDKTests/TokenTests.swift</c>.
/// </summary>
[TestFixture]
public sealed class UInt128EncodingTests
{
    [Test]
    public void EncodeDecode_roundtrips_for_canonical_test_values()
    {
        UInt128[] cases = [0, 1, 255, 256, 1000, 1_000_000, UInt128.MaxValue];
        foreach (var value in cases)
        {
            var encoded = TokenService.EncodeUInt128(value);
            encoded.Length.Should().Be(16, $"encoded length should be 16 for {value}");
            var decoded = TokenService.DecodeUInt128(encoded);
            decoded.Should().Be(value, $"roundtrip failed for {value}");
        }
    }

    [Test]
    public void Encode_1000_is_big_endian_with_low_word_0x03E8()
    {
        var encoded = TokenService.EncodeUInt128(1000).ToByteArray();
        encoded.Length.Should().Be(16);
        encoded[14].Should().Be(0x03);
        encoded[15].Should().Be(0xE8);

        for (int i = 0; i < 14; i++)
        {
            encoded[i].Should().Be(0, $"byte {i} should be 0");
        }
    }

    [Test]
    public void Encode_zero_produces_16_zero_bytes()
    {
        var encoded = TokenService.EncodeUInt128(UInt128.Zero).ToByteArray();
        encoded.Should().Equal(new byte[16]);
    }

    [Test]
    public void Decode_wrong_length_returns_zero()
    {
        // Defensive: protocol guarantees 16 bytes but a malformed wire
        // payload should not throw — falls back to zero, matching Swift.
        var bs = Google.Protobuf.ByteString.CopyFrom([0xff, 0xff]);
        TokenService.DecodeUInt128(bs).Should().Be(UInt128.Zero);
    }

    [Test]
    public void Decode_full_16_byte_max_returns_UInt128_MaxValue()
    {
        var bs = Google.Protobuf.ByteString.CopyFrom(Enumerable.Repeat<byte>(0xff, 16).ToArray());
        TokenService.DecodeUInt128(bs).Should().Be(UInt128.MaxValue);
    }
}
