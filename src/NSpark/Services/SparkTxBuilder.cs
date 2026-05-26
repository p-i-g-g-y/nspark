using uniffi.spark_frost;

namespace NSpark.Services;

/// <summary>
/// A constructed Bitcoin transaction with the precomputed sighash for its signing input.
/// Returned by the <see cref="SparkTxBuilder"/> methods that build Spark-protocol refund,
/// HTLC, and node transactions.
/// </summary>
/// <param name="Tx">Raw transaction bytes (segwit-serialised, ready for FROST signing).</param>
/// <param name="Sighash">32-byte SHA-256 sighash that the FROST signing round will sign.</param>
public sealed record SparkBitcoinTx(byte[] Tx, byte[] Sighash);

/// <summary>
/// Triple of refund transactions for a Spark leaf — CPFP-style, direct (optional, when the leaf
/// has a direct anchor), and direct-from-CPFP. The Spark protocol requires submitting all three
/// variants the leaf supports during transfer / claim / Lightning flows.
/// </summary>
public sealed record SparkRefundTxTrio(
    SparkBitcoinTx CpfpRefund,
    SparkBitcoinTx? DirectRefund,
    SparkBitcoinTx DirectFromCpfpRefund);

/// <summary>
/// Pair of root node transactions for a Spark deposit tree — CPFP + direct variants.
/// </summary>
public sealed record SparkNodeTxPair(SparkBitcoinTx Cpfp, SparkBitcoinTx Direct);

/// <summary>
/// Spark-protocol Bitcoin transaction construction. Builds refund txs, HTLC txs, deposit tree
/// node tx pairs, and multi-input sighashes from public inputs (parent tx bytes, public keys,
/// timelocks, fees). Backed by the native <c>spark_frost</c> Rust library.
/// </summary>
/// <remarks>
/// <para>
/// Every method on this class takes only public material — no private keys, no shares, no
/// nonces. The output is consumed by <see cref="FrostSigningHelper"/> to assemble FROST
/// signing jobs against the wallet's signer.
/// </para>
/// <para>
/// Custom <see cref="NSpark.Signer.ISparkSigner"/> implementations can also call these helpers
/// when they need to compute Spark-protocol transactions outside the default in-process flow.
/// </para>
/// </remarks>
public static class SparkTxBuilder
{
    /// <summary>
    /// Build the triple of refund transactions (CPFP, optional direct, direct-from-CPFP) that
    /// spend a leaf's node tx into a receiving public key under the given timelocks.
    /// </summary>
    /// <param name="cpfpNodeTx">Raw bytes of the leaf's CPFP node transaction.</param>
    /// <param name="directNodeTx">Raw bytes of the leaf's direct node transaction, or <c>null</c> when the leaf has no direct anchor.</param>
    /// <param name="vout">Output index inside the node tx that the refund spends.</param>
    /// <param name="receivingPublicKey">33-byte compressed pubkey the refund pays to.</param>
    /// <param name="network">Network identifier (<c>"mainnet"</c> or <c>"regtest"</c>).</param>
    /// <param name="sequence">nSequence (timelock) for the CPFP and direct-from-CPFP refunds.</param>
    /// <param name="directSequence">nSequence for the direct refund variant.</param>
    /// <param name="feeSats">Fee in satoshis applied to refund outputs that subtract a fee.</param>
    public static SparkRefundTxTrio BuildRefundTxTrio(
        byte[] cpfpNodeTx,
        byte[]? directNodeTx,
        uint vout,
        byte[] receivingPublicKey,
        string network,
        uint sequence,
        uint directSequence,
        ulong feeSats)
    {
        var result = SparkFrostMethods.ConstructRefundTxTrio(
            cpfpNodeTx: cpfpNodeTx,
            directNodeTx: directNodeTx,
            vout: vout,
            receivingPubkey: receivingPublicKey,
            network: network,
            sequence: sequence,
            directSequence: directSequence,
            feeSats: feeSats);

        return new SparkRefundTxTrio(
            CpfpRefund: ToSparkBitcoinTx(result.@cpfpRefund),
            DirectRefund: result.@directRefund is { } d ? ToSparkBitcoinTx(d) : null,
            DirectFromCpfpRefund: ToSparkBitcoinTx(result.@directFromCpfpRefund));
    }

    /// <summary>
    /// Build a Lightning HTLC transaction that spends a leaf's node tx into a hashlock/seqlock
    /// output pair (sender can claim by revealing the preimage; receiver can sweep after the
    /// HTLC sequence expires).
    /// </summary>
    /// <param name="nodeTx">Raw bytes of the parent node transaction.</param>
    /// <param name="vout">Output index inside <paramref name="nodeTx"/>.</param>
    /// <param name="sequence">nSequence (timelock) for the HTLC tx input.</param>
    /// <param name="paymentHash">32-byte SHA-256 of the Lightning preimage (BOLT11 <c>payment_hash</c>).</param>
    /// <param name="hashlockPubkey">33-byte compressed pubkey of the hashlock branch (typically the SSP).</param>
    /// <param name="seqlockPubkey">33-byte compressed pubkey of the timeout branch (typically the sender).</param>
    /// <param name="htlcSequence">HTLC-side relative timelock (protocol constant).</param>
    /// <param name="applyFee">Whether to subtract <paramref name="feeSats"/> from the HTLC output value.</param>
    /// <param name="feeSats">Fee in satoshis (used when <paramref name="applyFee"/> is true).</param>
    /// <param name="network">Network identifier (<c>"mainnet"</c> or <c>"regtest"</c>).</param>
    public static SparkBitcoinTx BuildHtlcTransaction(
        byte[] nodeTx,
        uint vout,
        uint sequence,
        byte[] paymentHash,
        byte[] hashlockPubkey,
        byte[] seqlockPubkey,
        uint htlcSequence,
        bool applyFee,
        ulong feeSats,
        string network)
    {
        var result = SparkFrostMethods.ConstructHtlcTransaction(
            nodeTx: nodeTx,
            vout: vout,
            sequence: sequence,
            paymentHash: paymentHash,
            hashlockPubkey: hashlockPubkey,
            seqlockPubkey: seqlockPubkey,
            htlcSequence: htlcSequence,
            applyFee: applyFee,
            feeSats: feeSats,
            network: network);

        return ToSparkBitcoinTx(result);
    }

    /// <summary>
    /// Build the root node transaction pair (CPFP + direct) for a fresh Spark deposit tree.
    /// </summary>
    /// <param name="parentTx">Raw bytes of the on-chain funding transaction.</param>
    /// <param name="vout">Output index inside <paramref name="parentTx"/> being spent.</param>
    /// <param name="address">Destination address for the root node output.</param>
    /// <param name="sequence">nSequence for the CPFP node tx input.</param>
    /// <param name="directSequence">nSequence for the direct node tx input.</param>
    /// <param name="feeSats">Fee in satoshis applied to the direct variant.</param>
    public static SparkNodeTxPair BuildNodeTxPair(
        byte[] parentTx,
        uint vout,
        string address,
        uint sequence,
        uint directSequence,
        ulong feeSats)
    {
        var result = SparkFrostMethods.ConstructNodeTxPair(
            parentTx: parentTx,
            vout: vout,
            address: address,
            sequence: sequence,
            directSequence: directSequence,
            feeSats: feeSats);

        return new SparkNodeTxPair(
            Cpfp: ToSparkBitcoinTx(result.@cpfp),
            Direct: ToSparkBitcoinTx(result.@direct));
    }

    /// <summary>
    /// Compute the BIP-341 (taproot) sighash for a multi-input transaction where each input
    /// references a previous output with its own scriptPubKey and value.
    /// </summary>
    /// <param name="tx">Raw bytes of the transaction being signed.</param>
    /// <param name="inputIndex">Zero-based index of the input to compute the sighash for.</param>
    /// <param name="prevOutScripts">scriptPubKeys of every input's previous output, in order.</param>
    /// <param name="prevOutValues">Output values (satoshis) of every input's previous output, in order.</param>
    public static byte[] ComputeMultiInputSighash(
        byte[] tx,
        uint inputIndex,
        IReadOnlyList<byte[]> prevOutScripts,
        IReadOnlyList<ulong> prevOutValues)
    {
        return SparkFrostMethods.ComputeMultiInputSighashUniffi(
            tx: tx,
            inputIndex: inputIndex,
            prevOutScripts: prevOutScripts.ToList(),
            prevOutValues: prevOutValues.ToList());
    }

    private static SparkBitcoinTx ToSparkBitcoinTx(TransactionResult result)
        => new(result.@tx, result.@sighash);
}
