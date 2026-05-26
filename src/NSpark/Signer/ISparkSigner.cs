namespace NSpark.Signer;

/// <summary>
/// The async signing contract for an NSpark wallet. Every cryptographic operation that
/// involves private key material — ECDSA over the identity key, ECIES decryption, per-leaf
/// FROST signing, tweak-share computation, deterministic Lightning preimages, static-deposit
/// key access, swap-adaptor key generation — is exposed here. NSpark's services consume only
/// this interface; nothing private leaks into the wallet process for remote implementations.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation, <see cref="SparkSigner"/>, is backed by a BIP-39 mnemonic
/// and NBitcoin / <c>spark_frost</c> primitives. Custom implementations targeting HSMs,
/// KMS-backed services, or hardware wallets only need to fulfil this interface — the rest
/// of NSpark stays unchanged.
/// </para>
/// <para>
/// All methods take a <see cref="CancellationToken"/> and return <see cref="Task"/> /
/// <see cref="Task{TResult}"/> so a remote signer can perform real network I/O without
/// blocking threads. Implementations MUST be safe for concurrent calls — NSpark issues
/// signer operations in parallel during leaf-heavy flows (transfers, withdrawals,
/// Lightning sends).
/// </para>
/// </remarks>
public interface ISparkSigner
{
    // ─────────────────────────────────────────────────────────────────────────
    // Identity key
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the identity public key (33-byte compressed secp256k1 point) used for
    /// authentication with Signing Operators, the SSP, and as the wallet's Spark address
    /// payload.
    /// </summary>
    Task<byte[]> GetIdentityPublicKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// ECDSA-sign a 32-byte message hash with the identity key, returning a DER-encoded
    /// signature. Used for SO / SSP challenge-response authentication and per-package
    /// signatures (token mint/transfer, transfer package, claim package, static-deposit
    /// claim payload).
    /// </summary>
    Task<byte[]> SignWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default);

    /// <summary>
    /// ECDSA-sign a 32-byte message hash with the identity key, returning a 64-byte
    /// compact <c>(r || s)</c> signature. Used for leaf key-tweak signatures in
    /// transfer / Lightning send / swap / withdrawal flows.
    /// </summary>
    Task<byte[]> SignCompactWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default);

    /// <summary>
    /// Decrypt an ECIES ciphertext addressed to the identity public key. Used when a
    /// Signing Operator returns intermediate signing-key material back to the receiver
    /// during the token claim flow.
    /// </summary>
    Task<byte[]> DecryptEciesWithIdentityKeyAsync(byte[] ciphertext, CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Deposit key (root of on-chain deposit tree)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the deposit public key (33-byte compressed secp256k1) used as the
    /// user-side spending key for the FROST-shared deposit tree.
    /// </summary>
    Task<byte[]> GetDepositPublicKeyAsync(CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Per-leaf signing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the public key (33-byte compressed) for the per-leaf signing key derived
    /// for <paramref name="leafId"/>. Implementations MUST be deterministic: the same
    /// <paramref name="leafId"/> always maps to the same public key.
    /// </summary>
    Task<byte[]> GetLeafPublicKeyAsync(string leafId, CancellationToken ct = default);

    /// <summary>
    /// Run a complete FROST signing round for a leaf in one call: generate the user's
    /// hiding/binding nonce, sign <paramref name="message"/> against the supplied SO
    /// commitments, and return both the public commitment and the user's partial signature.
    /// </summary>
    /// <param name="leafId">The leaf whose per-leaf signing key should sign.</param>
    /// <param name="message">The 32-byte sighash to sign.</param>
    /// <param name="verifyingKey">The FROST group verifying key for the leaf.</param>
    /// <param name="soCommitments">SO public commitments keyed by operator identifier.</param>
    /// <param name="adaptorPublicKey">Optional adaptor public key for swap-style signing.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LeafFrostSignature> SignLeafFrostAsync(
        string leafId,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing — phase 1. Generates the user's nonce commitment and
    /// returns an opaque <see cref="LeafFrostNonceCommitment.NonceHandle"/> the caller
    /// must pass back to <see cref="SignLeafFrostWithNonceAsync"/> once the SO sighash
    /// is available. Used by flows (e.g., cooperative-exit withdrawal) where the
    /// commitments must be sent to SOs before the final sighash is known.
    /// </summary>
    Task<LeafFrostNonceCommitment> GenerateLeafFrostNonceAsync(
        string leafId,
        CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing — phase 2. Signs <paramref name="message"/> using the
    /// nonce previously committed to by <see cref="GenerateLeafFrostNonceAsync"/>.
    /// The <paramref name="nonceHandle"/> MUST be the one returned from that call.
    /// </summary>
    Task<byte[]> SignLeafFrostWithNonceAsync(
        string leafId,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Compute leaf transfer tweak shares for the send side. The signer internally
    /// derives the leaf's current signing key, generates a new intermediate key,
    /// computes <c>(oldKey - newKey) mod n</c>, splits the tweak via VSS, and ECIES-
    /// encrypts the new intermediate key to <paramref name="receiverPublicKey"/>.
    /// </summary>
    /// <param name="leafId">The leaf being transferred.</param>
    /// <param name="receiverPublicKey">33-byte compressed public key the new intermediate key is encrypted to.</param>
    /// <param name="threshold">FROST threshold (number of SO shares required to recover the tweak).</param>
    /// <param name="numShares">Total number of SO shares to produce.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LeafTweakSharesResult> ComputeLeafTweakSharesAsync(
        string leafId,
        byte[] receiverPublicKey,
        uint threshold,
        uint numShares,
        CancellationToken ct = default);

    /// <summary>
    /// Compute claim-side tweak shares. The signer ECIES-decrypts the sender's
    /// <paramref name="senderSecretCipher"/> (which contains the sender's intermediate
    /// signing key), derives the receiver's new per-leaf signing key for <paramref name="leafId"/>,
    /// computes <c>(senderIntermediate - newLeafKey) mod n</c>, and splits the tweak via VSS.
    /// </summary>
    Task<ClaimTweakSharesResult> ComputeClaimTweakSharesAsync(
        string leafId,
        byte[] senderSecretCipher,
        uint threshold,
        uint numShares,
        CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Static deposit
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the public key (33-byte compressed) for the static-deposit signing key
    /// at <paramref name="index"/>.
    /// </summary>
    Task<byte[]> GetStaticDepositPublicKeyAsync(int index = 0, CancellationToken ct = default);

    /// <summary>
    /// Export the raw static-deposit private key as a 32-byte big-endian scalar.
    /// </summary>
    /// <remarks>
    /// The Spark static-deposit protocol requires revealing this key to the SSP so it
    /// can sweep the on-chain UTXO into the wallet's leaf. Signers that refuse to
    /// export raw key material (HSM/KMS) should throw <see cref="NotSupportedException"/>;
    /// static-deposit claims will then be unavailable on those signers.
    /// </remarks>
    /// <exception cref="NotSupportedException">When the signer cannot export the key.</exception>
    Task<byte[]> ExportStaticDepositPrivateKeyAsync(int index = 0, CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing for the static-deposit refund flow — phase 1.
    /// Generates the user's nonce commitment and returns an opaque handle the caller
    /// must pass back to <see cref="SignStaticDepositFrostWithNonceAsync"/>.
    /// </summary>
    Task<LeafFrostNonceCommitment> GenerateStaticDepositFrostNonceAsync(
        int index,
        CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing for the static-deposit refund flow — phase 2.
    /// Signs <paramref name="message"/> using the nonce previously committed to by
    /// <see cref="GenerateStaticDepositFrostNonceAsync"/>.
    /// </summary>
    Task<byte[]> SignStaticDepositFrostWithNonceAsync(
        int index,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Lightning preimage
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministically generate a Lightning HTLC preimage from <paramref name="transferId"/>
    /// and VSS-split it into per-SO shares with proofs. Only the SHA-256 payment hash and
    /// the shares cross the signer boundary; the preimage itself stays inside.
    /// </summary>
    /// <remarks>
    /// Determinism is load-bearing: if the wallet crashes between issuing the invoice and
    /// storing the shares, calling this method again with the same <paramref name="transferId"/>
    /// MUST produce the same payment hash so recovery is possible.
    /// </remarks>
    Task<PreimageShareSplitResult> CreatePreimageSharesAsync(
        string transferId,
        uint threshold,
        uint numShares,
        CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Adaptor key (swap completion)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a fresh ephemeral adaptor keypair used in atomic-swap flows. The wallet
    /// only sees the public key; the private half is referenced via the opaque handle
    /// for any subsequent operation that needs it.
    /// </summary>
    Task<AdaptorKeyHandle> GenerateAdaptorKeyAsync(CancellationToken ct = default);
}
