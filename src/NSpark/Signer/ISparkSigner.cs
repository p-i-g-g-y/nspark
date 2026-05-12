namespace NSpark.Signer;

/// <inheritdoc/>
public interface ISparkSigner
{
    /// <summary>
    /// The identity public key (33-byte compressed secp256k1) used for authentication.
    /// </summary>
    public byte[] IdentityPublicKey { get; }

    /// <summary>
    /// The identity private key (32-byte secp256k1 scalar) for ECIES decryption.
    /// </summary>
    public byte[] IdentityPrivateKey { get; }

    /// <summary>
    /// The deposit public key (33-byte compressed secp256k1) used for on-chain deposits.
    /// </summary>
    public byte[] DepositPublicKey { get; }

    /// <summary>
    /// ECDSA-sign a message hash with the identity key. Returns DER-encoded signature.
    /// Used for challenge-response authentication with Signing Operators.
    /// </summary>
    public byte[] SignWithIdentityKey(byte[] messageHash);

    /// <summary>
    /// ECDSA-sign a message hash with the identity key. Returns compact signature (64 bytes, r||s).
    /// Used for leaf key tweak signatures (SendLeafKeyTweak.signature).
    /// </summary>
    public byte[] SignCompactWithIdentityKey(byte[] messageHash);

    /// <summary>
    /// Derive a per-leaf signing private key from a leaf node ID.
    /// Path: m/8797555'/{account}'/1'/{sha256(leafId)[0:4] % 2^31 + 2^31}
    /// </summary>
    public byte[] DeriveLeafSigningKey(string leafId);

    /// <summary>
    /// Derive a static deposit private key for a given index.
    /// Path: m/8797555'/{account}'/3'/{2^31 + idx}
    /// </summary>
    public byte[] DeriveStaticDepositKey(int index);

    /// <summary>
    /// Generate a deterministic HTLC preimage via HMAC-SHA256(htlcKey, transferId).
    /// </summary>
    public byte[] GeneratePreimage(string transferId);

    /// <summary>
    /// FROST threshold signing — delegates to native library.
    /// Throws NotImplementedException until native lib is available.
    /// </summary>
    public byte[] FrostSign(byte[] message, byte[] signingKey, byte[] groupKey, byte[] commitmentSeed);

    /// <summary>
    /// Generate FROST signing commitments (hiding + binding nonces).
    /// Throws NotImplementedException until native lib is available.
    /// </summary>
    public (byte[] hiding, byte[] binding) GenerateFrostCommitments(byte[] signingKey);
}
