using Google.Protobuf;
using NSpark.Proto;
using uniffi.spark_frost;
using KeyPackage = uniffi.spark_frost.KeyPackage;
using SigningCommitment = uniffi.spark_frost.SigningCommitment;

namespace NSpark.Services;

/// <summary>
/// Shared helper for FROST threshold signing rounds with Spark Signing Operators.
/// Handles: get SO commitments → generate user nonce → sign → build UserSignedTxSigningJob.
/// </summary>
internal static class FrostSigningHelper
{
    /// <summary>
    /// Build a UserSignedTxSigningJob for a single transaction (e.g., a refund tx).
    /// This performs the user's side of the FROST signing round:
    /// 1. Create KeyPackage from the leaf's signing key + verifying key
    /// 2. Generate user nonce/commitment
    /// 3. Sign with FROST using the sighash and SO commitments
    /// </summary>
    internal static UserSignedTxSigningJob BuildSigningJob(
        string leafId,
        byte[] signingKey,
        byte[] verifyingKey,
        byte[] rawTx,
        byte[] sighash,
        IReadOnlyDictionary<string, NSpark.Proto.Common.SigningCommitment> soCommitments)
    {
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
        var keyPackage = new KeyPackage(
            secretKey: signingKey,
            publicKey: publicKey,
            verifyingKey: verifyingKey);

        var nonceResult = SparkFrostMethods.FrostNonce(keyPackage);

        // Convert proto SO commitments to native SigningCommitment type
        var nativeCommitments = new Dictionary<string, SigningCommitment>();
        foreach (var (soId, protoCommitment) in soCommitments)
        {
            nativeCommitments[soId] = new SigningCommitment(
                hiding: protoCommitment.Hiding.ToByteArray(),
                binding: protoCommitment.Binding.ToByteArray());
        }

        var userSignature = SparkFrostMethods.SignFrost(
            msg: sighash,
            keyPackage: keyPackage,
            nonce: nonceResult.@nonce,
            selfCommitment: nonceResult.@commitment,
            statechainCommitments: nativeCommitments,
            adaptorPublicKey: null);

        // Build the proto signing commitments (SO commitments map)
        var protoSigningCommitments = new SigningCommitments();
        foreach (var (soId, commitment) in soCommitments)
        {
            protoSigningCommitments.SigningCommitments_.Add(soId, commitment);
        }

        return new UserSignedTxSigningJob
        {
            LeafId = leafId,
            SigningPublicKey = ByteString.CopyFrom(publicKey),
            RawTx = ByteString.CopyFrom(rawTx),
            SigningNonceCommitment = new NSpark.Proto.Common.SigningCommitment
            {
                Hiding = ByteString.CopyFrom(nonceResult.@commitment.@hiding),
                Binding = ByteString.CopyFrom(nonceResult.@commitment.@binding),
            },
            UserSignature = ByteString.CopyFrom(userSignature),
            SigningCommitments = protoSigningCommitments,
        };
    }

    /// <summary>
    /// Build a SigningJob (for start_deposit_tree_creation — just the commitment, no user signature yet).
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
            SigningNonceCommitment = new NSpark.Proto.Common.SigningCommitment
            {
                Hiding = ByteString.CopyFrom(hidingNonce),
                Binding = ByteString.CopyFrom(bindingNonce),
            },
        };
    }

    /// <summary>
    /// Sign FROST and aggregate with SO signing results from cooperative_exit_v2.
    /// Used when the SOs return their partial signatures and we need to combine with ours.
    /// </summary>
    internal static byte[] SignAndAggregateFrost(
        byte[] sighash,
        byte[] signingKey,
        byte[] verifyingKey,
        NonceResult nonce,
        NSpark.Proto.SigningResult signingResult)
    {
        var selfPublicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
        var keyPackage = new KeyPackage(
            secretKey: signingKey,
            publicKey: selfPublicKey,
            verifyingKey: verifyingKey);

        // Convert proto SO commitments to native
        var nativeCommitments = new Dictionary<string, SigningCommitment>();
        foreach (var (soId, protoCommitment) in signingResult.SigningNonceCommitments)
        {
            nativeCommitments[soId] = new SigningCommitment(
                hiding: protoCommitment.Hiding.ToByteArray(),
                binding: protoCommitment.Binding.ToByteArray());
        }

        var selfSignature = SparkFrostMethods.SignFrost(
            msg: sighash,
            keyPackage: keyPackage,
            nonce: nonce.@nonce,
            selfCommitment: nonce.@commitment,
            statechainCommitments: nativeCommitments,
            adaptorPublicKey: null);

        // Convert SO signature shares and public keys
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
            statechainCommitments: nativeCommitments,
            selfCommitment: nonce.@commitment,
            statechainSignatures: soSignatures,
            selfSignature: selfSignature,
            statechainPublicKeys: soPublicKeys,
            selfPublicKey: selfPublicKey,
            verifyingKey: verifyingKey,
            adaptorPublicKey: null);
    }

    /// <summary>
    /// Build a UserSignedTxSigningJob with adaptor public key for swap flows.
    /// Returns the job plus the self-commitment and sighash needed for later FROST aggregation.
    /// </summary>
    internal static (UserSignedTxSigningJob Job, uniffi.spark_frost.SigningCommitment SelfCommitment, byte[] Sighash)
        BuildSigningJobWithAdaptor(
            string leafId,
            byte[] signingKey,
            byte[] verifyingKey,
            byte[] rawTx,
            byte[] sighash,
            IReadOnlyDictionary<string, NSpark.Proto.Common.SigningCommitment> soCommitments,
            byte[] adaptorPublicKey)
    {
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
        var keyPackage = new KeyPackage(
            secretKey: signingKey,
            publicKey: publicKey,
            verifyingKey: verifyingKey);

        var nonceResult = SparkFrostMethods.FrostNonce(keyPackage);

        var nativeCommitments = new Dictionary<string, SigningCommitment>();
        foreach (var (soId, protoCommitment) in soCommitments)
        {
            nativeCommitments[soId] = new SigningCommitment(
                hiding: protoCommitment.Hiding.ToByteArray(),
                binding: protoCommitment.Binding.ToByteArray());
        }

        var userSignature = SparkFrostMethods.SignFrost(
            msg: sighash,
            keyPackage: keyPackage,
            nonce: nonceResult.@nonce,
            selfCommitment: nonceResult.@commitment,
            statechainCommitments: nativeCommitments,
            adaptorPublicKey: adaptorPublicKey);

        var protoSigningCommitments = new SigningCommitments();
        foreach (var (soId, commitment) in soCommitments)
        {
            protoSigningCommitments.SigningCommitments_.Add(soId, commitment);
        }

        var job = new UserSignedTxSigningJob
        {
            LeafId = leafId,
            SigningPublicKey = ByteString.CopyFrom(publicKey),
            RawTx = ByteString.CopyFrom(rawTx),
            SigningNonceCommitment = new NSpark.Proto.Common.SigningCommitment
            {
                Hiding = ByteString.CopyFrom(nonceResult.@commitment.@hiding),
                Binding = ByteString.CopyFrom(nonceResult.@commitment.@binding),
            },
            UserSignature = ByteString.CopyFrom(userSignature),
            SigningCommitments = protoSigningCommitments,
        };

        return (job, nonceResult.@commitment, sighash);
    }

    /// <summary>
    /// Aggregate FROST signatures from SO signing results with an adaptor public key.
    /// Used after initiate_swap_primary_transfer returns SO partial signatures.
    /// </summary>
    internal static byte[] AggregateFrostWithAdaptor(
        byte[] sighash,
        uniffi.spark_frost.SigningCommitment selfCommitment,
        byte[] selfSignature,
        byte[] selfPublicKey,
        byte[] verifyingKey,
        NSpark.Proto.SigningResult signingResult,
        byte[] adaptorPublicKey)
    {
        var nativeCommitments = new Dictionary<string, SigningCommitment>();
        foreach (var (soId, protoCommitment) in signingResult.SigningNonceCommitments)
        {
            nativeCommitments[soId] = new SigningCommitment(
                hiding: protoCommitment.Hiding.ToByteArray(),
                binding: protoCommitment.Binding.ToByteArray());
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
            statechainCommitments: nativeCommitments,
            selfCommitment: selfCommitment,
            statechainSignatures: soSignatures,
            selfSignature: selfSignature,
            statechainPublicKeys: soPublicKeys,
            selfPublicKey: selfPublicKey,
            verifyingKey: verifyingKey,
            adaptorPublicKey: adaptorPublicKey);
    }

    /// <summary>
    /// Get the network string ("mainnet" or "regtest") for native lib calls.
    /// </summary>
    internal static string GetNetworkString(SparkNetwork network)
    {
        return network == SparkNetwork.Mainnet ? "mainnet" : "regtest";
    }
}
