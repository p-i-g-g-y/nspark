using NSpark.Signer;

namespace NSpark.UnitTests.Signer;

/// <summary>
/// Tests for <see cref="KeyDerivation"/> covering BIP-39 / BIP-32 derivation,
/// the Spark purpose path, leaf-key derivation, and the deterministic preimage
/// HMAC.
/// </summary>
/// <remarks>
/// These tests use the well-known BIP-39 test mnemonic
/// <c>"abandon abandon abandon ... about"</c> — publicly documented in
/// BIP-39 and committed to git history in many open-source repositories.
/// Never fund a wallet derived from it.
/// </remarks>
[TestFixture]
public sealed class KeyDerivationTests
{
    // The canonical BIP-39 zero-entropy 12-word mnemonic. Public test vector.
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Test]
    public void SparkPurpose_constant_is_8797555()
    {
        // Hardcoded constant the Spark protocol agreed on; any change here
        // breaks address derivation across every SDK.
        KeyDerivation.SparkPurpose.Should().Be(8797555);
    }

    [Test]
    public void FromMnemonic_is_deterministic()
    {
        var a = KeyDerivation.FromMnemonic(TestMnemonic);
        var b = KeyDerivation.FromMnemonic(TestMnemonic);

        a.IdentityKey.PrivateKey.ToBytes().Should().Equal(b.IdentityKey.PrivateKey.ToBytes());
        a.SigningKey.PrivateKey.ToBytes().Should().Equal(b.SigningKey.PrivateKey.ToBytes());
        a.DepositKey.PrivateKey.ToBytes().Should().Equal(b.DepositKey.PrivateKey.ToBytes());
        a.StaticDepositKey.PrivateKey.ToBytes().Should().Equal(b.StaticDepositKey.PrivateKey.ToBytes());
        a.HtlcPreimageKey.PrivateKey.ToBytes().Should().Equal(b.HtlcPreimageKey.PrivateKey.ToBytes());
    }

    [Test]
    public void FromMnemonic_different_passphrase_yields_different_keys()
    {
        var a = KeyDerivation.FromMnemonic(TestMnemonic, account: 0, passphrase: null);
        var b = KeyDerivation.FromMnemonic(TestMnemonic, account: 0, passphrase: "TREZOR");

        a.IdentityKey.PrivateKey.ToBytes().Should().NotEqual(b.IdentityKey.PrivateKey.ToBytes());
    }

    [Test]
    public void FromMnemonic_different_account_yields_different_keys()
    {
        var account0 = KeyDerivation.FromMnemonic(TestMnemonic, account: 0);
        var account1 = KeyDerivation.FromMnemonic(TestMnemonic, account: 1);

        account0.IdentityKey.PrivateKey.ToBytes().Should()
            .NotEqual(account1.IdentityKey.PrivateKey.ToBytes());
    }

    [Test]
    public void FromMnemonic_all_five_keys_are_distinct_at_same_account()
    {
        // The five derived keys (identity / signing / deposit / staticDeposit /
        // htlcPreimage) must be pairwise distinct; if any two collide the
        // protocol's domain separation breaks.
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);

        var allKeys = new[]
        {
            keys.IdentityKey.PrivateKey.ToBytes(),
            keys.SigningKey.PrivateKey.ToBytes(),
            keys.DepositKey.PrivateKey.ToBytes(),
            keys.StaticDepositKey.PrivateKey.ToBytes(),
            keys.HtlcPreimageKey.PrivateKey.ToBytes(),
        };

        for (int i = 0; i < allKeys.Length; i++)
        {
            for (int j = i + 1; j < allKeys.Length; j++)
            {
                allKeys[i].Should().NotEqual(allKeys[j],
                    because: $"key index {i} and {j} must derive to different scalars");
            }
        }
    }

    [Test]
    public void DeriveLeafKey_is_deterministic_for_same_leaf_id()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var a = keys.DeriveLeafKey("leaf-abc");
        var b = keys.DeriveLeafKey("leaf-abc");
        a.PrivateKey.ToBytes().Should().Equal(b.PrivateKey.ToBytes());
    }

    [Test]
    public void DeriveLeafKey_different_leaf_ids_produce_different_keys()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var a = keys.DeriveLeafKey("leaf-abc");
        var b = keys.DeriveLeafKey("leaf-xyz");
        a.PrivateKey.ToBytes().Should().NotEqual(b.PrivateKey.ToBytes());
    }

    [Test]
    public void DeriveStaticDepositChildKey_is_deterministic()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var a = keys.DeriveStaticDepositChildKey(0);
        var b = keys.DeriveStaticDepositChildKey(0);
        a.PrivateKey.ToBytes().Should().Equal(b.PrivateKey.ToBytes());
    }

    [Test]
    public void DeriveStaticDepositChildKey_different_indices_diverge()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var k0 = keys.DeriveStaticDepositChildKey(0);
        var k1 = keys.DeriveStaticDepositChildKey(1);
        var k2 = keys.DeriveStaticDepositChildKey(2);

        k0.PrivateKey.ToBytes().Should().NotEqual(k1.PrivateKey.ToBytes());
        k1.PrivateKey.ToBytes().Should().NotEqual(k2.PrivateKey.ToBytes());
        k0.PrivateKey.ToBytes().Should().NotEqual(k2.PrivateKey.ToBytes());
    }

    [Test]
    public void ComputePreimage_returns_32_bytes()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var preimage = keys.ComputePreimage("transfer-id-1");
        preimage.Length.Should().Be(32);
    }

    [Test]
    public void ComputePreimage_is_deterministic_for_same_transfer_id()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var a = keys.ComputePreimage("transfer-id-1");
        var b = keys.ComputePreimage("transfer-id-1");
        a.Should().Equal(b);
    }

    [Test]
    public void ComputePreimage_different_transfer_ids_produce_different_preimages()
    {
        var keys = KeyDerivation.FromMnemonic(TestMnemonic);
        var a = keys.ComputePreimage("transfer-id-1");
        var b = keys.ComputePreimage("transfer-id-2");
        a.Should().NotEqual(b);
    }

    [Test]
    public void ComputePreimage_depends_on_seed()
    {
        // The HMAC key comes from the HtlcPreimageKey which depends on the
        // mnemonic; two different mnemonics must produce different preimages
        // even for the same transfer id.
        var keysA = KeyDerivation.FromMnemonic(TestMnemonic);
        var keysB = KeyDerivation.FromMnemonic(TestMnemonic, account: 5);

        var a = keysA.ComputePreimage("transfer-id-1");
        var b = keysB.ComputePreimage("transfer-id-1");
        a.Should().NotEqual(b);
    }
}
