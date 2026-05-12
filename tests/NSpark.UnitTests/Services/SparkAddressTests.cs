using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="SparkAddress.DecodeIdentityPublicKey"/>.
/// </summary>
[TestFixture]
public sealed class SparkAddressTests
{
    [Test]
    public void Decode_round_trips_identity_pubkey_through_synthetic_address()
    {
        // Build a synthetic Spark-address-shaped payload: protobuf field 1
        // (wire type 2 = length-delimited) carrying a 33-byte pubkey.
        var pubKey = new byte[33];
        for (int i = 0; i < 33; i++) pubKey[i] = (byte)(i + 1);

        var payload = new byte[35];
        payload[0] = 0x0A;    // field 1, wire type 2
        payload[1] = 33;      // length
        Array.Copy(pubKey, 0, payload, 2, 33);

        var encoded = Bech32mHelper.Encode("spark", payload);

        var decoded = SparkAddress.DecodeIdentityPublicKey(encoded);
        decoded.Should().Equal(pubKey);
    }

    [Test]
    public void Decode_throws_when_payload_does_not_start_with_field1_tag()
    {
        // Wrong tag (field 2 = 0x12 instead of field 1 = 0x0A).
        var payload = new byte[35];
        payload[0] = 0x12;
        payload[1] = 33;
        var encoded = Bech32mHelper.Encode("spark", payload);

        Action act = () => SparkAddress.DecodeIdentityPublicKey(encoded);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*protobuf field 1 tag*");
    }

    [Test]
    public void Decode_throws_when_payload_shorter_than_declared_length()
    {
        // Declared length 33 but only 10 bytes follow.
        var payload = new byte[12];
        payload[0] = 0x0A;
        payload[1] = 33;
        var encoded = Bech32mHelper.Encode("spark", payload);

        Action act = () => SparkAddress.DecodeIdentityPublicKey(encoded);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*too short*");
    }
}
