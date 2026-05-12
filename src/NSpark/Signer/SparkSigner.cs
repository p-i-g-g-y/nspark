using NBitcoin;
using uniffi.spark_frost;

namespace NSpark.Signer;

/// <summary>
/// Wraps NBitcoin for key derivation and delegates FROST crypto to the native spark_frost library
/// via auto-generated UniFFI bindings.
/// </summary>
public sealed class SparkSigner : ISparkSigner
{
    private readonly KeyDerivation _keys;

    /// <inheritdoc/>
    public SparkSigner(KeyDerivation keys)
    {
        _keys = keys;
    }

    /// <inheritdoc/>
    public static SparkSigner FromMnemonic(string mnemonic, int account = 0, string? passphrase = null)
    {
        var keys = KeyDerivation.FromMnemonic(mnemonic, account, passphrase);
        return new SparkSigner(keys);
    }

    /// <inheritdoc/>
    public byte[] IdentityPublicKey => _keys.IdentityKey.GetPublicKey().ToBytes();

    /// <inheritdoc/>
    public byte[] IdentityPrivateKey => _keys.IdentityKey.PrivateKey.ToBytes();

    /// <inheritdoc/>
    public byte[] DepositPublicKey => _keys.DepositKey.GetPublicKey().ToBytes();

    /// <inheritdoc/>
    public byte[] SignWithIdentityKey(byte[] messageHash)
    {
        var hash = new uint256(messageHash);
        var sig = _keys.IdentityKey.PrivateKey.Sign(hash);
        return sig.ToDER();
    }

    /// <inheritdoc/>
    public byte[] SignCompactWithIdentityKey(byte[] messageHash)
    {
        var hash = new uint256(messageHash);
        var sig = _keys.IdentityKey.PrivateKey.Sign(hash);
        return sig.ToCompact();
    }

    /// <inheritdoc/>
    public byte[] DeriveLeafSigningKey(string leafId)
    {
        var leafKey = _keys.DeriveLeafKey(leafId);
        return leafKey.PrivateKey.ToBytes();
    }

    /// <inheritdoc/>
    public byte[] DeriveStaticDepositKey(int index)
    {
        var key = _keys.DeriveStaticDepositChildKey(index);
        return key.PrivateKey.ToBytes();
    }

    /// <inheritdoc/>
    public byte[] GeneratePreimage(string transferId)
    {
        return _keys.ComputePreimage(transferId);
    }

    /// <inheritdoc/>
    public byte[] FrostSign(
        byte[] message,
        byte[] signingKey,
        byte[] groupKey,
        byte[] commitmentSeed)
    {
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
        var keyPackage = new KeyPackage(
            secretKey: signingKey,
            publicKey: publicKey,
            verifyingKey: groupKey
        );

        var nonceResult = SparkFrostMethods.FrostNonce(keyPackage);

        return SparkFrostMethods.SignFrost(
            msg: message,
            keyPackage: keyPackage,
            nonce: nonceResult.@nonce,
            selfCommitment: nonceResult.@commitment,
            statechainCommitments: new Dictionary<string, SigningCommitment>(),
            adaptorPublicKey: null);
    }

    /// <inheritdoc/>
    public (byte[] hiding, byte[] binding) GenerateFrostCommitments(byte[] signingKey)
    {
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
        var keyPackage = new KeyPackage(
            secretKey: signingKey,
            publicKey: publicKey,
            verifyingKey: signingKey // placeholder — real verifying key comes from SO
        );

        var nonceResult = SparkFrostMethods.FrostNonce(keyPackage);
        return (nonceResult.@commitment.@hiding, nonceResult.@commitment.@binding);
    }
}
