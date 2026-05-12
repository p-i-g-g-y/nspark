using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>
/// Encode and decode 32-byte Spark token identifiers as Bech32m strings
/// (e.g. <c>"btkn1..."</c> on mainnet, <c>"btknrt1..."</c> on regtest).
/// </summary>
/// <remarks>
/// Matches the Swift / Kotlin / TS Spark SDKs so token identifiers
/// round-trip across language clients.
/// </remarks>
public static class TokenIdentifier
{
    private const string MainnetPrefix = "btkn";
    private const string RegtestPrefix = "btknrt";
    private const int IdentifierLength = 32;
    private const int Bech32mLimit = 500;

    /// <summary>Return the Bech32m HRP for the given network.</summary>
    public static string PrefixFor(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => MainnetPrefix,
        SparkNetwork.Regtest => RegtestPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    /// <summary>
    /// Decode an HRP back to the originating <see cref="SparkNetwork"/>.
    /// Returns <c>null</c> for prefixes reserved by the protocol but not
    /// supported in v1 (testnet / signet / local).
    /// </summary>
    public static SparkNetwork? NetworkFor(string prefix) => prefix switch
    {
        MainnetPrefix => SparkNetwork.Mainnet,
        RegtestPrefix => SparkNetwork.Regtest,
        "btknt" or "btkns" or "btknl" => null, // reserved
        _ => null,
    };

    /// <summary>
    /// Encode a raw 32-byte token identifier as a Bech32m string for the
    /// given network.
    /// </summary>
    /// <exception cref="SparkConfigurationException">
    /// Thrown when <paramref name="rawIdentifier"/> is not exactly 32 bytes.
    /// </exception>
    public static string Encode(byte[] rawIdentifier, SparkNetwork network)
    {
        ArgumentNullException.ThrowIfNull(rawIdentifier);
        if (rawIdentifier.Length != IdentifierLength)
        {
            throw new SparkConfigurationException(
                "token.identifier.encode",
                $"Token identifier must be {IdentifierLength} bytes, got {rawIdentifier.Length}.");
        }

        return Bech32mHelper.Encode(PrefixFor(network), rawIdentifier);
    }

    /// <summary>
    /// Decode a Bech32m token identifier into its raw 32-byte form, returning
    /// the detected <see cref="SparkNetwork"/>. Pass <paramref name="expectedNetwork"/>
    /// to assert the prefix matches a specific network.
    /// </summary>
    /// <exception cref="SparkConfigurationException">
    /// Thrown when the prefix is unrecognized, the data length is wrong, or
    /// the prefix does not match the expected network.
    /// </exception>
    public static (byte[] RawIdentifier, SparkNetwork Network) Decode(
        string bech32mIdentifier,
        SparkNetwork? expectedNetwork = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bech32mIdentifier);

        var (hrp, raw) = Bech32mHelper.Decode(bech32mIdentifier, Bech32mLimit);

        if (expectedNetwork is { } wanted)
        {
            var expectedHrp = PrefixFor(wanted);
            if (!string.Equals(hrp, expectedHrp, StringComparison.Ordinal))
            {
                throw new SparkConfigurationException(
                    "token.identifier.decode",
                    $"Invalid token identifier prefix: expected '{expectedHrp}', got '{hrp}'.");
            }
        }

        var detectedNetwork = NetworkFor(hrp) ?? throw new SparkConfigurationException(
            "token.identifier.decode",
            $"Unknown token identifier prefix: '{hrp}'.");

        if (raw.Length != IdentifierLength)
        {
            throw new SparkConfigurationException(
                "token.identifier.decode",
                $"Token identifier must be {IdentifierLength} bytes, got {raw.Length}.");
        }

        return (raw, detectedNetwork);
    }
}
