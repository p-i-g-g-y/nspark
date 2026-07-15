using NSpark.Proto;
using NSpark.Services;

namespace NSpark.Models;

/// <summary>
/// A single Spark leaf — a UTXO-equivalent unit of value held by the wallet.
/// </summary>
/// <param name="Id">Unique leaf identifier assigned by the Signing Operators.</param>
/// <param name="TreeId">Identifier of the tree this leaf belongs to.</param>
/// <param name="ValueSats">Value of the leaf in satoshis.</param>
/// <param name="Status">Server-reported status string (e.g. <c>"AVAILABLE"</c>).</param>
public sealed record SparkLeaf(string Id, string TreeId, long ValueSats, string Status)
{
    /// <summary>
    /// The underlying protobuf TreeNode for this leaf. Internal because the
    /// generated proto types are an implementation detail; they are not part
    /// of the public NSpark API contract.
    /// </summary>
    internal TreeNode Node { get; init; } = null!;

    /// <summary>
    /// Remaining refund-tx timelock in blocks. Below 200 the leaf needs
    /// renewal; at or below 100 it cannot move at all until renewed. See
    /// <c>RenewalService.RenewExhaustedLeavesAsync</c>.
    /// </summary>
    public uint RefundTimelockBlocks => ClaimService.ExtractRefundSequence(Node) & 0xFFFF;
}
