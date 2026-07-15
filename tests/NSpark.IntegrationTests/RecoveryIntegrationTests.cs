using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Proto;
using NSpark.Services;

namespace NSpark.Tests;

// =============================================================================
// Recovery snapshot tests (matching Swift: IntegrationTests RecoveryTests)
// Read-only against mainnet: queries nodes, never moves funds.
// =============================================================================

[TestFixture]
[Category("Integration")]
public class RecoveryTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;

    private SparkConnection _client = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task RecoverySnapshotCoversBalanceAndCompleteChains()
    {
        var wallet = await _client.CreateWalletAsync(MnemonicA);
        var balance = await wallet.GetBalanceAsync();
        var snapshot = await wallet.GetRecoverySnapshotAsync();

        TestContext.Out.WriteLine(
            $"Snapshot: {snapshot.Leaves.Count} leaves, {snapshot.Nodes.Count} ancestors, " +
            $"{snapshot.TotalLeafSats} sats (available: {balance.SatsBalance.Available})");

        Assert.That(snapshot.Network, Is.EqualTo("MAINNET"));
        Assert.That(snapshot.IdentityPublicKeyHex, Is.EqualTo(wallet.IdentityPublicKeyHex));
        Assert.That(snapshot.TotalLeafSats, Is.GreaterThanOrEqualTo(balance.SatsBalance.Available));

        // Every entry decodes; every leaf carries the txs an exit package needs.
        var byId = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes)
        {
            byId[node.Id] = TreeNode.Parser.ParseFrom(Convert.FromHexString(node.TreeNodeHex));
        }

        foreach (var leaf in snapshot.Leaves)
        {
            var decoded = TreeNode.Parser.ParseFrom(Convert.FromHexString(leaf.TreeNodeHex));
            Assert.That(decoded.NodeTx.IsEmpty, Is.False, $"leaf {leaf.Id} has no node tx");
            Assert.That(decoded.RefundTx.IsEmpty, Is.False, $"leaf {leaf.Id} has no refund tx");
            byId[leaf.Id] = decoded;
        }

        // Every leaf chain terminates at a root within a sane hop count.
        foreach (var leaf in snapshot.Leaves)
        {
            var cursor = byId[leaf.Id];
            var hops = 0;
            while (cursor.HasParentNodeId && cursor.ParentNodeId.Length > 0)
            {
                var parentId = cursor.ParentNodeId;
                Assert.That(byId.ContainsKey(parentId), Is.True,
                    $"chain above leaf {leaf.Id} is missing ancestor {parentId}");
                cursor = byId[parentId];
                hops++;
                Assert.That(hops, Is.LessThan(100),
                    $"chain above leaf {leaf.Id} does not terminate at a root");
            }
        }
    }
}
