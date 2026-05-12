using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TokenIdentifier"/> ported from the Swift Spark SDK
/// (<c>TokenTests.swift</c>) to keep .NET and Swift identifier encoding in
/// lockstep.
/// </summary>
[TestFixture]
public sealed class TokenIdentifierTests
{
    private static readonly byte[] SampleId32 = Convert.FromHexString(
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890");

    [Test]
    public void Encode_decode_mainnet_round_trips()
    {
        var encoded = TokenIdentifier.Encode(SampleId32, SparkNetwork.Mainnet);
        encoded.Should().StartWith("btkn1");

        var (decoded, network) = TokenIdentifier.Decode(encoded, SparkNetwork.Mainnet);
        decoded.Should().Equal(SampleId32);
        network.Should().Be(SparkNetwork.Mainnet);
    }

    [Test]
    public void Encode_decode_regtest_round_trips()
    {
        var rawId = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000001");

        var encoded = TokenIdentifier.Encode(rawId, SparkNetwork.Regtest);
        encoded.Should().StartWith("btknrt1");

        var (decoded, network) = TokenIdentifier.Decode(encoded);
        decoded.Should().Equal(rawId);
        network.Should().Be(SparkNetwork.Regtest);
    }

    [TestCase("Mainnet")]
    [TestCase("Regtest")]
    public void Roundtrip_random_data_for_each_network(string networkName)
    {
        var network = Enum.Parse<SparkNetwork>(networkName);
        var rawId = Convert.FromHexString(
            "deadbeefcafebabe1234567890abcdef1122334455667788aabbccddeeff0011");

        var encoded = TokenIdentifier.Encode(rawId, network);
        var (decoded, detectedNetwork) = TokenIdentifier.Decode(encoded);

        decoded.Should().Equal(rawId, $"roundtrip failed for {network}");
        detectedNetwork.Should().Be(network);
    }

    [Test]
    public void Encode_throws_on_short_identifier()
    {
        var shortId = Convert.FromHexString("abcdef");
        Action act = () => TokenIdentifier.Encode(shortId, SparkNetwork.Mainnet);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*must be 32 bytes*");
    }

    [Test]
    public void Encode_throws_on_oversized_identifier()
    {
        var longId = new byte[40];
        Action act = () => TokenIdentifier.Encode(longId, SparkNetwork.Mainnet);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*must be 32 bytes*");
    }

    [Test]
    public void Decode_throws_when_expected_network_does_not_match_prefix()
    {
        var encoded = TokenIdentifier.Encode(SampleId32, SparkNetwork.Mainnet);

        Action act = () => TokenIdentifier.Decode(encoded, SparkNetwork.Regtest);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*expected*btknrt*got*btkn*");
    }

    [Test]
    public void Decode_throws_on_unknown_prefix()
    {
        // Use Bech32mHelper directly to produce a valid-checksum string with a
        // bogus HRP so we exercise the network-prefix-not-recognized branch.
        var encoded = Bech32mHelper.Encode("foo", SampleId32);
        Action act = () => TokenIdentifier.Decode(encoded);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*Unknown token identifier prefix*");
    }

    [Test]
    public void PrefixFor_returns_canonical_strings()
    {
        TokenIdentifier.PrefixFor(SparkNetwork.Mainnet).Should().Be("btkn");
        TokenIdentifier.PrefixFor(SparkNetwork.Regtest).Should().Be("btknrt");
    }

    [TestCase("btkn", SparkNetwork.Mainnet)]
    [TestCase("btknrt", SparkNetwork.Regtest)]
    public void NetworkFor_round_trips_canonical_prefix(string prefix, SparkNetwork expected)
    {
        TokenIdentifier.NetworkFor(prefix).Should().Be(expected);
    }

    [TestCase("btknt")] // testnet, not supported
    [TestCase("btkns")] // signet, not supported
    [TestCase("btknl")] // local, not supported
    [TestCase("bc")]    // unrelated bitcoin HRP
    public void NetworkFor_returns_null_for_unsupported_or_unknown_prefix(string prefix)
    {
        TokenIdentifier.NetworkFor(prefix).Should().BeNull();
    }
}
