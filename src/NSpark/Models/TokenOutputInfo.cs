namespace NSpark.Models;

/// <summary>
/// A token transaction output (TTXO) owned by the wallet, with a reference
/// back to the transaction that produced it.
/// </summary>
/// <param name="Id">Server-assigned output id, or <c>null</c> for partial/unconfirmed outputs.</param>
/// <param name="OwnerPublicKey">33-byte compressed secp256k1 public key of the owner.</param>
/// <param name="TokenIdentifier">Raw 32-byte token identifier (use <see cref="NSpark.Services.TokenIdentifier"/> for Bech32m).</param>
/// <param name="TokenAmount">Amount held by this output as a 128-bit unsigned integer.</param>
/// <param name="PreviousTransactionHash">32-byte hash of the transaction that created this output.</param>
/// <param name="PreviousTransactionVout">Output index within the previous transaction.</param>
/// <param name="Status">Server-reported status (e.g. <c>"AVAILABLE"</c>, <c>"SPENT"</c>, <c>"FROZEN"</c>).</param>
public sealed record TokenOutputInfo(
    string? Id,
    byte[] OwnerPublicKey,
    byte[] TokenIdentifier,
    UInt128 TokenAmount,
    byte[] PreviousTransactionHash,
    uint PreviousTransactionVout,
    string Status);
