namespace NSpark.Models;

/// <summary>
/// Holdings of a single token type owned by this wallet.
/// </summary>
/// <param name="TokenMetadata">Descriptor of the token (identifier, decimals, max supply, …).</param>
/// <param name="OwnedBalance">
/// All token units the wallet owns. <see cref="UInt128"/> matches the
/// protocol-level amount representation (16-byte big-endian unsigned).
/// </param>
/// <param name="AvailableToSendBalance">
/// Token units immediately available to spend — excludes outputs locked in
/// in-flight transfers.
/// </param>
public sealed record TokenBalance(
    TokenMetadata TokenMetadata,
    UInt128 OwnedBalance,
    UInt128 AvailableToSendBalance);
