using uniffi.spark_frost;
using FrostSigningCommitment = uniffi.spark_frost.SigningCommitment;

namespace NSpark.Signer;

/// <summary>
/// Pure-public-key FROST signature aggregation. Combines the user's partial signature with the
/// Signing Operators' partial signatures into the final aggregated FROST signature that verifies
/// under the leaf's group verifying key.
/// </summary>
/// <remarks>
/// <para>
/// Aggregation involves no private key material — it operates entirely on public commitments,
/// public keys, and partial signatures. It is therefore safe to run outside any signer trust
/// boundary, and custom <see cref="ISparkSigner"/> implementations (HSM, KMS, remote service)
/// can call this directly when they need to finalise a 2-phase signing flow.
/// </para>
/// <para>
/// Backed by the native <c>spark_frost</c> Rust library. No <c>uniffi</c> types are exposed
/// on this API.
/// </para>
/// </remarks>
public static class FrostAggregator
{
    /// <summary>
    /// Aggregate the user's partial FROST signature with the SOs' partial signatures into the
    /// final signature for <paramref name="message"/> under <paramref name="verifyingKey"/>.
    /// </summary>
    /// <param name="message">32-byte sighash that was signed.</param>
    /// <param name="statechainCommitments">SO public commitments keyed by operator identifier.</param>
    /// <param name="selfCommitment">User's public commitment that was published to the SOs.</param>
    /// <param name="statechainSignatures">SO partial signatures keyed by operator identifier.</param>
    /// <param name="selfSignature">User's partial signature.</param>
    /// <param name="statechainPublicKeys">SO public keys keyed by operator identifier.</param>
    /// <param name="selfPublicKey">User's leaf public key (33-byte compressed secp256k1).</param>
    /// <param name="verifyingKey">FROST group verifying key for the leaf.</param>
    /// <param name="adaptorPublicKey">Optional adaptor public key used in atomic-swap signing.</param>
    /// <returns>The final 64-byte aggregated FROST signature.</returns>
    public static byte[] Aggregate(
        byte[] message,
        IReadOnlyDictionary<string, SigningCommitment> statechainCommitments,
        SigningCommitment selfCommitment,
        IReadOnlyDictionary<string, byte[]> statechainSignatures,
        byte[] selfSignature,
        IReadOnlyDictionary<string, byte[]> statechainPublicKeys,
        byte[] selfPublicKey,
        byte[] verifyingKey,
        byte[]? adaptorPublicKey = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(statechainCommitments);
        ArgumentNullException.ThrowIfNull(selfCommitment);
        ArgumentNullException.ThrowIfNull(statechainSignatures);
        ArgumentNullException.ThrowIfNull(selfSignature);
        ArgumentNullException.ThrowIfNull(statechainPublicKeys);
        ArgumentNullException.ThrowIfNull(selfPublicKey);
        ArgumentNullException.ThrowIfNull(verifyingKey);

        var statechainNative = new Dictionary<string, FrostSigningCommitment>(statechainCommitments.Count);
        foreach (var (id, c) in statechainCommitments)
        {
            statechainNative[id] = new FrostSigningCommitment(c.Hiding, c.Binding);
        }

        return SparkFrostMethods.AggregateFrost(
            msg: message,
            statechainCommitments: statechainNative,
            selfCommitment: new FrostSigningCommitment(selfCommitment.Hiding, selfCommitment.Binding),
            statechainSignatures: statechainSignatures.ToDictionary(kv => kv.Key, kv => kv.Value),
            selfSignature: selfSignature,
            statechainPublicKeys: statechainPublicKeys.ToDictionary(kv => kv.Key, kv => kv.Value),
            selfPublicKey: selfPublicKey,
            verifyingKey: verifyingKey,
            adaptorPublicKey: adaptorPublicKey);
    }
}
