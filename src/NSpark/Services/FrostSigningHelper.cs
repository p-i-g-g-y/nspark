using Google.Protobuf;
using NSpark.Proto;
using NSpark.Signer;
using uniffi.spark_frost;
using FrostSigningCommitment = uniffi.spark_frost.SigningCommitment;
using ProtoSigningCommitment = NSpark.Proto.Common.SigningCommitment;
using SignerSigningCommitment = NSpark.Signer.SigningCommitment;

namespace NSpark.Services;

/// <summary>
/// Helpers around <see cref="ISparkSigner"/>'s FROST surface: build <see cref="UserSignedTxSigningJob"/>
/// instances for the SO-side protocol, convert between proto and signer commitment types,
/// and combine self+SO partial signatures via the public aggregation primitive.
/// </summary>
/// <remarks>
/// This helper does NOT touch private key material. Every operation that needs a private
/// scalar (FROST nonce generation, FROST partial signing) is delegated to <see cref="ISparkSigner"/>.
/// </remarks>
internal static class FrostSigningHelper
{
    /// <summary>
    /// Build a <see cref="UserSignedTxSigningJob"/> by running a complete one-shot FROST
    /// signing round on the supplied leaf: generate the user's nonce + sign against the
    /// provided SO commitments + the transaction sighash. The signing key never leaves
    /// <paramref name="signer"/>.
    /// </summary>
    internal static async Task<UserSignedTxSigningJob> BuildSigningJobAsync(
        ISparkSigner signer,
        string leafId,
        byte[] verifyingKey,
        byte[] rawTx,
        byte[] sighash,
        IReadOnlyDictionary<string, ProtoSigningCommitment> soCommitments,
        CancellationToken ct)
    {
        var commitments = ConvertSoCommitments(soCommitments);
        var result = await signer.SignLeafFrostAsync(
            leafId, sighash, verifyingKey, commitments, adaptorPublicKey: null, ct)
            .ConfigureAwait(false);

        return BuildJob(leafId, rawTx, soCommitments, result);
    }

    /// <summary>
    /// One-shot FROST signing with an adaptor public key (used in atomic-swap flows).
    /// Returns the signing job plus the self-commitment and sighash so a later
    /// <see cref="AggregateAdaptorAsync"/> call can finalise the aggregated signature.
    /// </summary>
    internal static async Task<(UserSignedTxSigningJob Job, SignerSigningCommitment SelfCommitment, byte[] Sighash)>
        BuildSigningJobWithAdaptorAsync(
            ISparkSigner signer,
            string leafId,
            byte[] verifyingKey,
            byte[] rawTx,
            byte[] sighash,
            IReadOnlyDictionary<string, ProtoSigningCommitment> soCommitments,
            byte[] adaptorPublicKey,
            CancellationToken ct)
    {
        var commitments = ConvertSoCommitments(soCommitments);
        var result = await signer.SignLeafFrostAsync(
            leafId, sighash, verifyingKey, commitments, adaptorPublicKey, ct)
            .ConfigureAwait(false);
        var job = BuildJob(leafId, rawTx, soCommitments, result);
        return (job, result.Commitment, sighash);
    }

    /// <summary>
    /// Aggregate self + SO partial FROST signatures into a final aggregated signature.
    /// This is a pure-public-key operation — no signer involvement required.
    /// </summary>
    internal static byte[] AggregateFrostSignature(
        byte[] sighash,
        SignerSigningCommitment selfCommitment,
        byte[] selfSignature,
        byte[] selfPublicKey,
        byte[] verifyingKey,
        SigningResult signingResult,
        byte[]? adaptorPublicKey = null)
    {
        var soCommitments = new Dictionary<string, FrostSigningCommitment>();
        foreach (var (soId, c) in signingResult.SigningNonceCommitments)
        {
            soCommitments[soId] = new FrostSigningCommitment(c.Hiding.ToByteArray(), c.Binding.ToByteArray());
        }

        var soSignatures = new Dictionary<string, byte[]>();
        foreach (var (soId, sig) in signingResult.SignatureShares)
        {
            soSignatures[soId] = sig.ToByteArray();
        }

        var soPublicKeys = new Dictionary<string, byte[]>();
        foreach (var (soId, pk) in signingResult.PublicKeys)
        {
            soPublicKeys[soId] = pk.ToByteArray();
        }

        return SparkFrostMethods.AggregateFrost(
            msg: sighash,
            statechainCommitments: soCommitments,
            selfCommitment: new FrostSigningCommitment(selfCommitment.Hiding, selfCommitment.Binding),
            statechainSignatures: soSignatures,
            selfSignature: selfSignature,
            statechainPublicKeys: soPublicKeys,
            selfPublicKey: selfPublicKey,
            verifyingKey: verifyingKey,
            adaptorPublicKey: adaptorPublicKey);
    }

    /// <summary>
    /// Build an unsigned <see cref="SigningJob"/> that only carries the nonce commitment
    /// (used by deposit tree creation where the user signs only the root + refund).
    /// </summary>
    internal static SigningJob BuildUnsignedJob(
        byte[] signingPublicKey,
        byte[] rawTx,
        byte[] hidingNonce,
        byte[] bindingNonce)
    {
        return new SigningJob
        {
            SigningPublicKey = ByteString.CopyFrom(signingPublicKey),
            RawTx = ByteString.CopyFrom(rawTx),
            SigningNonceCommitment = new ProtoSigningCommitment
            {
                Hiding = ByteString.CopyFrom(hidingNonce),
                Binding = ByteString.CopyFrom(bindingNonce),
            },
        };
    }

    /// <summary>
    /// Convert a proto <see cref="ProtoSigningCommitment"/> dictionary into the signer's
    /// <see cref="SignerSigningCommitment"/> shape.
    /// </summary>
    internal static IReadOnlyDictionary<string, SignerSigningCommitment> ConvertSoCommitments(
        IReadOnlyDictionary<string, ProtoSigningCommitment> protoCommitments)
    {
        var result = new Dictionary<string, SignerSigningCommitment>(protoCommitments.Count);
        foreach (var (id, c) in protoCommitments)
        {
            result[id] = new SignerSigningCommitment(c.Hiding.ToByteArray(), c.Binding.ToByteArray());
        }
        return result;
    }

    /// <summary>
    /// Network string ("mainnet" or "regtest") for native lib calls.
    /// </summary>
    internal static string GetNetworkString(SparkNetwork network)
        => network == SparkNetwork.Mainnet ? "mainnet" : "regtest";

    // ─────────────────────────────────────────────────────────────────────────
    // Internal builders
    // ─────────────────────────────────────────────────────────────────────────

    private static UserSignedTxSigningJob BuildJob(
        string leafId,
        byte[] rawTx,
        IReadOnlyDictionary<string, ProtoSigningCommitment> soCommitments,
        LeafFrostSignature signature)
    {
        var protoSigningCommitments = new SigningCommitments();
        foreach (var (soId, c) in soCommitments)
        {
            protoSigningCommitments.SigningCommitments_.Add(soId, c);
        }

        return new UserSignedTxSigningJob
        {
            LeafId = leafId,
            SigningPublicKey = ByteString.CopyFrom(signature.PublicKey),
            RawTx = ByteString.CopyFrom(rawTx),
            SigningNonceCommitment = new ProtoSigningCommitment
            {
                Hiding = ByteString.CopyFrom(signature.Commitment.Hiding),
                Binding = ByteString.CopyFrom(signature.Commitment.Binding),
            },
            UserSignature = ByteString.CopyFrom(signature.UserSignature),
            SigningCommitments = protoSigningCommitments,
        };
    }
}
