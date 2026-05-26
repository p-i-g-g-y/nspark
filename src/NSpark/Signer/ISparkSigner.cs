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
    public Task<byte[]> GetIdentityPublicKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// ECDSA-sign a 32-byte message hash with the identity key, returning a DER-encoded
    /// signature. Used for SO / SSP challenge-response authentication and per-package
    /// signatures (token mint/transfer, transfer package, claim package, static-deposit
    /// claim payload).
    /// </summary>
    public Task<byte[]> SignWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default);

    /// <summary>
    /// ECDSA-sign a 32-byte message hash with the identity key, returning a 64-byte
    /// compact <c>(r || s)</c> signature. Used for leaf key-tweak signatures in
    /// transfer / Lightning send / swap / withdrawal flows.
    /// </summary>
    public Task<byte[]> SignCompactWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default);

    /// <summary>
    /// Decrypt an ECIES ciphertext addressed to the identity public key. Used when a
    /// Signing Operator returns intermediate signing-key material back to the receiver
    /// during the token claim flow.
    /// </summary>
    public Task<byte[]> DecryptEciesWithIdentityKeyAsync(byte[] ciphertext, CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Deposit key (root of on-chain deposit tree)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the deposit public key (33-byte compressed secp256k1) used as the
    /// user-side spending key for the FROST-shared deposit tree.
    /// </summary>
    public Task<byte[]> GetDepositPublicKeyAsync(CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Per-leaf signing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the public key (33-byte compressed) for the per-leaf signing key derived
    /// for <paramref name="leafId"/>. Implementations MUST be deterministic: the same
    /// <paramref name="leafId"/> always maps to the same public key.
    /// </summary>
    public Task<byte[]> GetLeafPublicKeyAsync(string leafId, CancellationToken ct = default);

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
    public Task<LeafFrostSignature> SignLeafFrostAsync(
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
    public Task<LeafFrostNonceCommitment> GenerateLeafFrostNonceAsync(
        string leafId,
        CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing — phase 2. Signs <paramref name="message"/> using the
    /// nonce previously committed to by <see cref="GenerateLeafFrostNonceAsync"/>.
    /// The <paramref name="nonceHandle"/> MUST be the one returned from that call.
    /// </summary>
    public Task<byte[]> SignLeafFrostWithNonceAsync(
        string leafId,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Build a complete send-side tweak batch and return per-SO ECIES-encrypted
    /// <c>SendLeafKeyTweaks</c> proto blobs ready for the wire. For each leaf the signer
    /// derives the leaf signing key, generates a fresh intermediate key, computes the
    /// tweak <c>(oldKey - newKey) mod n</c>, VSS-splits the tweak, ECIES-encrypts the
    /// intermediate key to the receiver, signs the per-leaf tweak payload, and folds
    /// every leaf's contribution into one <c>SendLeafKeyTweaks</c> proto per SO before
    /// ECIES-encrypting it to that SO's identity public key.
    /// </summary>
    /// <remarks>
    /// No plaintext share material, no intermediate-key plaintext, and no tweak signature
    /// payload leaves the signer — the wallet only sees the final encrypted blobs to plug
    /// into the <c>key_tweak_package</c> map.
    /// </remarks>
    /// <param name="leaves">One descriptor per leaf in the batch.</param>
    /// <param name="soTargets">SO targets the encrypted packages will be addressed to (one entry per SO).</param>
    /// <param name="transferId">Transfer ID — bound into the per-leaf tweak signature payload.</param>
    /// <param name="threshold">FROST threshold (minimum SO shares required to reconstruct the tweak).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<EncryptedSendTweakBatch> BuildEncryptedSendTweaksAsync(
        IReadOnlyList<SendTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        string transferId,
        uint threshold,
        CancellationToken ct = default);

    /// <summary>
    /// Build a complete claim-side tweak batch. The signer ECIES-decrypts each leaf's
    /// <see cref="ClaimTweakLeafDescriptor.SenderSecretCipher"/> to obtain the sender's
    /// intermediate signing key, derives the receiver's new per-leaf key, computes the
    /// tweak, VSS-splits it, and folds everything into one <c>ClaimLeafKeyTweaks</c>
    /// proto per SO before ECIES-encrypting it to that SO.
    /// </summary>
    /// <remarks>
    /// The per-leaf new public key is returned so the wallet can use it as the receiving
    /// pubkey when constructing claim refund txs — no other plaintext key material leaves
    /// the signer.
    /// </remarks>
    public Task<EncryptedClaimTweakBatch> BuildEncryptedClaimTweaksAsync(
        IReadOnlyList<ClaimTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Static deposit
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the public key (33-byte compressed) for the static-deposit signing key
    /// at <paramref name="index"/>.
    /// </summary>
    public Task<byte[]> GetStaticDepositPublicKeyAsync(int index = 0, CancellationToken ct = default);

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
    public Task<byte[]> ExportStaticDepositPrivateKeyAsync(int index = 0, CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing for the static-deposit refund flow — phase 1.
    /// Generates the user's nonce commitment and returns an opaque handle the caller
    /// must pass back to <see cref="SignStaticDepositFrostWithNonceAsync"/>.
    /// </summary>
    public Task<LeafFrostNonceCommitment> GenerateStaticDepositFrostNonceAsync(
        int index,
        CancellationToken ct = default);

    /// <summary>
    /// Two-phase FROST signing for the static-deposit refund flow — phase 2.
    /// Signs <paramref name="message"/> using the nonce previously committed to by
    /// <see cref="GenerateStaticDepositFrostNonceAsync"/>.
    /// </summary>
    public Task<byte[]> SignStaticDepositFrostWithNonceAsync(
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
    /// Build a per-SO ECIES-encrypted Lightning preimage share bundle. The signer
    /// deterministically derives the preimage from <paramref name="transferId"/>,
    /// VSS-splits it into one share per SO, wraps each share with its proofs in a
    /// <c>SecretShare</c> proto, and ECIES-encrypts that proto to the matching SO's
    /// identity public key. Only the payment hash and the per-SO encrypted blobs cross
    /// the signer boundary; the preimage and the raw share scalars stay inside.
    /// </summary>
    /// <remarks>
    /// Determinism is load-bearing: if the wallet crashes between issuing the invoice
    /// and storing the shares, calling this method again with the same
    /// <paramref name="transferId"/> MUST produce the same payment hash so recovery
    /// is possible.
    /// </remarks>
    public Task<EncryptedPreimageShareBundle> BuildEncryptedPreimageSharesAsync(
        string transferId,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Adaptor key (swap completion)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a fresh ephemeral adaptor keypair used in atomic-swap flows. The wallet
    /// only sees the public key; the private half is referenced via the opaque handle
    /// for any subsequent operation that needs it.
    /// </summary>
    public Task<AdaptorKeyHandle> GenerateAdaptorKeyAsync(CancellationToken ct = default);
}
