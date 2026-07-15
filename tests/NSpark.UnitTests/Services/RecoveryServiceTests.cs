using Google.Protobuf;
using NSpark.Proto;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="RecoveryService"/>'s pure snapshot-classification
/// logic, mirroring the Swift SDK's RecoveryServiceTests: leaves vs ancestors,
/// hex round-trips, missing-parent detection, and pruning of historical nodes.
/// </summary>
[TestFixture]
public sealed class RecoveryServiceTests
{
    private static readonly byte[] Me = MakeKey(0x02);
    private static readonly byte[] Them = MakeKey(0x03);

    [Test]
    public void Snapshot_classifies_leaves_vs_ancestors_and_round_trips_hex()
    {
        // root -> mid -> leaf chain, plus a locked leaf directly under root.
        var root = MakeNode("root", parent: null, owner: Me, status: "SPLITTED");
        var mid = MakeNode("mid", parent: "root", owner: Me, status: "SPLITTED");
        var leaf = MakeNode("leaf", parent: "mid", owner: Me, status: "AVAILABLE", value: 5000);
        var locked = MakeNode("locked", parent: "root", owner: Me, status: "TRANSFER_LOCKED", value: 250);

        var all = ToMap(root, mid, leaf, locked);
        RecoveryService.MissingParentIds(all).Should().BeEmpty();

        var snapshot = RecoveryService.BuildRecoverySnapshot(all, Me, "MAINNET");

        snapshot.Network.Should().Be("MAINNET");
        snapshot.IdentityPublicKeyHex.Should().Be(Convert.ToHexString(Me).ToLowerInvariant());
        snapshot.Leaves.Select(l => l.Id).Should().Equal("leaf", "locked");
        snapshot.Nodes.Select(n => n.Id).Should().Equal("mid", "root");
        snapshot.TotalLeafSats.Should().Be(5250);

        // Hex must decode back to an identical TreeNode carrying the refund tx.
        var leafHex = snapshot.Leaves.Single(l => l.Id == "leaf").TreeNodeHex;
        var decoded = TreeNode.Parser.ParseFrom(Convert.FromHexString(leafHex));
        decoded.Should().Be(leaf);
        decoded.RefundTx.IsEmpty.Should().BeFalse();
    }

    [Test]
    public void Missing_parents_are_detected_so_the_repair_pass_can_refetch_them()
    {
        // Leaf references a parent the bulk query omitted (legacy-root gotcha).
        var leaf = MakeNode("leaf", parent: "ghost-root", owner: Me, status: "AVAILABLE");

        var missing = RecoveryService.MissingParentIds(ToMap(leaf));

        missing.Should().BeEquivalentTo("ghost-root");
    }

    [Test]
    public void Foreign_owned_nodes_never_classify_as_leaves()
    {
        var foreign = MakeNode("foreign", parent: null, owner: Them, status: "AVAILABLE");

        var snapshot = RecoveryService.BuildRecoverySnapshot(ToMap(foreign), Me, "MAINNET");

        snapshot.Leaves.Should().BeEmpty();
        snapshot.Nodes.Should().BeEmpty();
    }

    [Test]
    public void Historical_nodes_off_the_current_chains_are_pruned()
    {
        var root = MakeNode("root", parent: null, owner: Me, status: "SPLITTED");
        var leaf = MakeNode("leaf", parent: "root", owner: Me, status: "AVAILABLE");
        // Old split intermediate under the same root whose sats moved on long
        // ago: nobody's parent and not owned-status, so no exit package needs it.
        var stale = MakeNode("stale", parent: "root", owner: Me, status: "SPLITTED");
        // A whole disconnected historical tree.
        var oldRoot = MakeNode("old-root", parent: null, owner: Me, status: "SPLITTED");

        var snapshot = RecoveryService.BuildRecoverySnapshot(
            ToMap(root, leaf, stale, oldRoot), Me, "MAINNET");

        snapshot.Leaves.Select(l => l.Id).Should().Equal("leaf");
        snapshot.Nodes.Select(n => n.Id).Should().Equal("root");
    }

    [Test]
    public void A_hole_in_a_needed_chain_throws_instead_of_producing_a_broken_bundle()
    {
        var leaf = MakeNode("leaf", parent: "ghost", owner: Me, status: "AVAILABLE");

        var act = () => RecoveryService.BuildRecoverySnapshot(ToMap(leaf), Me, "MAINNET");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing ancestor ghost above leaf leaf*");
    }

    private static byte[] MakeKey(byte fill)
    {
        var key = new byte[33];
        Array.Fill(key, fill);
        return key;
    }

    private static TreeNode MakeNode(
        string id, string? parent, byte[] owner, string status, ulong value = 1000)
    {
        var node = new TreeNode
        {
            Id = id,
            TreeId = "tree-1",
            Value = value,
            NodeTx = ByteString.CopyFrom(0xde, 0xad),
            RefundTx = ByteString.CopyFrom(0xbe, 0xef),
            OwnerIdentityPublicKey = ByteString.CopyFrom(owner),
            Status = status,
        };
        if (parent != null)
        {
            node.ParentNodeId = parent;
        }

        return node;
    }

    private static Dictionary<string, TreeNode> ToMap(params TreeNode[] nodes)
    {
        return nodes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
    }
}
