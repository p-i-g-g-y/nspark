using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TokenService.CollectOperatorIdentityPublicKeys"/>.
/// </summary>
[TestFixture]
public sealed class OperatorKeysTests
{
    [Test]
    public void Mainnet_wallet_collects_three_sorted_operator_keys()
    {
        var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
        using var connection = new SparkConnection(options, new HttpClient());
        var wallet = connection.CreateWallet(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

        var keys = TokenService.CollectOperatorIdentityPublicKeys(wallet);
        keys.Should().HaveCount(3);

        // Lexicographic sort.
        for (int i = 0; i < keys.Count - 1; i++)
        {
            CompareBytes(keys[i], keys[i + 1]).Should().BeLessThan(0,
                because: $"key {i} ({Hex(keys[i])}) must lexicographically precede key {i + 1} ({Hex(keys[i + 1])})");
        }
    }

    [Test]
    public void Regtest_wallet_skips_operators_with_empty_identity_pubkey()
    {
        // Regtest defaults have empty identity pubkey hex strings — they should be skipped.
        // Note: setting Network alone doesn't auto-switch SigningOperators (each
        // SparkOptions property defaults independently), so the test wires the
        // regtest operators explicitly.
        var options = Options.Create(new SparkOptions
        {
            Network = SparkNetwork.Regtest,
            SigningOperators = SparkOptions.GetDefaultOperators(SparkNetwork.Regtest),
        });
        using var connection = new SparkConnection(options, new HttpClient());
        var wallet = connection.CreateWallet(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

        var keys = TokenService.CollectOperatorIdentityPublicKeys(wallet);
        keys.Should().BeEmpty();
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
