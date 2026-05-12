using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>
/// Helpers for working with Spark addresses (<c>spark1...</c> on mainnet,
/// <c>sparkrt1...</c> on regtest). The address payload is a protobuf-encoded
/// message containing the receiver's identity public key as field 1.
/// </summary>
public static class SparkAddress
{
    /// <summary>
    /// Decode a Spark address and extract the 33-byte identity public key it carries.
    /// </summary>
    /// <param name="sparkAddress">A bech32m-encoded Spark address.</param>
    /// <returns>The 33-byte compressed secp256k1 identity public key.</returns>
    /// <exception cref="SparkConfigurationException">
    /// Thrown when the address cannot be decoded or the embedded protobuf payload
    /// does not start with the expected <c>identity_public_key</c> tag (10) +
    /// length prefix.
    /// </exception>
    public static byte[] DecodeIdentityPublicKey(string sparkAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(sparkAddress);

        var (_, payload) = Bech32mHelper.Decode(sparkAddress, limit: 500);

        // Protobuf wire format: field 1, wire type 2 (length-delimited) = tag 0x0A.
        if (payload.Length < 2 || payload[0] != 0x0A)
        {
            throw new SparkConfigurationException(
                "spark.address.decode",
                "Invalid Spark address payload (expected protobuf field 1 tag).");
        }

        int keyLen = payload[1];
        if (payload.Length < 2 + keyLen)
        {
            throw new SparkConfigurationException(
                "spark.address.decode",
                $"Spark address payload too short: declared key length {keyLen}, have {payload.Length - 2}.");
        }

        var key = new byte[keyLen];
        Array.Copy(payload, 2, key, 0, keyLen);
        return key;
    }
}
