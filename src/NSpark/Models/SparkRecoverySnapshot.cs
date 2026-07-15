namespace NSpark.Models;

/// <summary>
/// A leaf currently owned by the wallet, with its full TreeNode encoded for
/// offline use (raw node tx, pre-signed refund txs, verifying key, parent id).
/// </summary>
/// <param name="Id">Leaf node identifier.</param>
/// <param name="Status">Server-reported status at snapshot time.</param>
/// <param name="ValueSats">Leaf value in satoshis.</param>
/// <param name="TreeNodeHex">Lowercase hex of the protobuf-serialized TreeNode.</param>
public sealed record SparkRecoveryLeaf(string Id, string Status, long ValueSats, string TreeNodeHex);

/// <summary>An ancestor node on the path from a leaf to its tree root.</summary>
/// <param name="Id">Node identifier.</param>
/// <param name="TreeNodeHex">Lowercase hex of the protobuf-serialized TreeNode.</param>
public sealed record SparkRecoveryNode(string Id, string TreeNodeHex);

/// <summary>
/// Everything besides the seed needed to unilaterally exit the wallet's funds
/// while Spark operators are offline. Leaves cannot be re-discovered from the
/// seed once operators are down, so this snapshot must be captured while they
/// are online and refreshed whenever the leaf set changes.
/// </summary>
/// <param name="Network"><c>"MAINNET"</c> or <c>"REGTEST"</c>.</param>
/// <param name="IdentityPublicKeyHex">Owning wallet's identity public key, lowercase hex.</param>
/// <param name="Leaves">Current leaves, sorted by id for deterministic output.</param>
/// <param name="Nodes">Ancestors pruned to the union of the leaves' parent chains, sorted by id.</param>
public sealed record SparkRecoverySnapshot(
    string Network,
    string IdentityPublicKeyHex,
    IReadOnlyList<SparkRecoveryLeaf> Leaves,
    IReadOnlyList<SparkRecoveryNode> Nodes)
{
    /// <summary>Total value covered by the snapshot's leaves, in satoshis.</summary>
    public long TotalLeafSats => Leaves.Sum(l => l.ValueSats);
}
