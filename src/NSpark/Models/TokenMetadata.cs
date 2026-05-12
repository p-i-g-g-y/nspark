namespace NSpark.Models;

/// <summary>
/// Metadata describing a single token type (LRC-20 / Spark token).
/// </summary>
/// <param name="TokenIdentifier">
/// Bech32m-encoded human-readable identifier (e.g. <c>"sptt1..."</c> for
/// mainnet, <c>"sprtt1..."</c> for regtest).
/// </param>
/// <param name="RawTokenIdentifier">The raw 33-byte token identifier as stored in the protocol.</param>
/// <param name="IssuerPublicKey">Public key of the token issuer (33 bytes, compressed secp256k1).</param>
/// <param name="TokenName">Human-readable token name (e.g. "Spark Test Token").</param>
/// <param name="TokenTicker">Short ticker symbol (e.g. "SPTT").</param>
/// <param name="Decimals">Number of fractional decimal places.</param>
/// <param name="MaxSupply">Maximum total supply as a 16-byte big-endian uint128.</param>
/// <param name="IsFreezable">True if the issuer can freeze outputs of this token.</param>
/// <param name="ExtraMetadata">Optional issuer-defined metadata blob, format unspecified.</param>
public sealed record TokenMetadata(
    string TokenIdentifier,
    byte[] RawTokenIdentifier,
    byte[] IssuerPublicKey,
    string TokenName,
    string TokenTicker,
    uint Decimals,
    byte[] MaxSupply,
    bool IsFreezable,
    byte[]? ExtraMetadata = null);
