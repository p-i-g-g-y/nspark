using NSpark.Models;

namespace NSpark.UnitTests.Models;

/// <summary>
/// Tests for <see cref="SatsBalance"/> and <see cref="WalletBalance"/> ensuring
/// the data model matches the Swift / Kotlin / TS Spark SDKs.
/// </summary>
[TestFixture]
public sealed class SatsBalanceTests
{
    [Test]
    public void SatsBalance_constructor_round_trips()
    {
        var b = new SatsBalance(Available: 100, Owned: 250, Incoming: 50);
        b.Available.Should().Be(100);
        b.Owned.Should().Be(250);
        b.Incoming.Should().Be(50);
    }

    [Test]
    public void WalletBalance_holds_sats_balance_token_balances_and_leaves()
    {
        var sats = new SatsBalance(1000, 1500, 200);
        var tokens = Array.Empty<TokenBalance>();
        var leaves = new List<SparkLeaf>
        {
            new("leaf-1", "tree-a", 500, "AVAILABLE"),
            new("leaf-2", "tree-a", 500, "AVAILABLE"),
        };

        var wb = new WalletBalance(sats, tokens, leaves);

        wb.SatsBalance.Should().BeSameAs(sats);
        wb.TokenBalances.Should().BeSameAs(tokens);
        wb.Leaves.Should().BeSameAs(leaves);
    }

#pragma warning disable CS0618 // intentional test of [Obsolete] shim
    [Test]
    public void WalletBalance_TotalSats_obsolete_shim_returns_owned()
    {
        var wb = new WalletBalance(
            new SatsBalance(100, 250, 50),
            Array.Empty<TokenBalance>(),
            []);

        wb.TotalSats.Should().Be(250);
    }
#pragma warning restore CS0618

    [Test]
    public void WalletBalance_records_are_value_equal_on_same_inputs()
    {
        var sats = new SatsBalance(1, 2, 3);
        var tokens = Array.Empty<TokenBalance>();
        IReadOnlyList<SparkLeaf> leaves = [];

        var a = new WalletBalance(sats, tokens, leaves);
        var b = new WalletBalance(sats, tokens, leaves);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
