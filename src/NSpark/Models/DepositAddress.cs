namespace NSpark.Models;

/// <summary>A freshly generated on-chain deposit address that, once funded, can be claimed into a Spark leaf.</summary>
/// <param name="Address">Bech32m-encoded P2TR address to send Bitcoin to.</param>
/// <param name="LeafId">Identifier of the Spark leaf the deposit will mint when claimed.</param>
/// <param name="UserPublicKey">User-side signing public key associated with this address.</param>
/// <param name="VerifyingKey">FROST group verifying key for the leaf.</param>
public sealed record DepositAddress(
    string Address,
    string LeafId,
    byte[] UserPublicKey,
    byte[] VerifyingKey);

/// <summary>An on-chain address that can receive multiple deposits, each of which is later claimed individually.</summary>
/// <param name="Address">Bech32m-encoded P2TR address.</param>
/// <param name="VerifyingKey">FROST group verifying key for the static deposit.</param>
public sealed record StaticDepositAddress(
    string Address,
    byte[] VerifyingKey);

/// <summary>A specific on-chain output observed at a static deposit address.</summary>
/// <param name="Txid">Transaction id (hex, big-endian display form).</param>
/// <param name="Vout">Output index within the transaction.</param>
public sealed record DepositUtxo(
    string Txid,
    uint Vout);
