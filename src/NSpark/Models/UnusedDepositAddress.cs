namespace NSpark.Models;

/// <summary>
/// A previously generated deposit address that has not yet seen a confirmed
/// on-chain deposit, returned by the SO when querying for outstanding addresses.
/// </summary>
/// <param name="Address">Bech32m-encoded P2TR address.</param>
/// <param name="LeafId">Identifier of the prospective leaf, when known.</param>
/// <param name="UserSigningPublicKey">User-side signing public key.</param>
/// <param name="VerifyingPublicKey">FROST group verifying public key.</param>
public sealed record UnusedDepositAddress(
    string Address,
    string? LeafId,
    byte[] UserSigningPublicKey,
    byte[] VerifyingPublicKey);
