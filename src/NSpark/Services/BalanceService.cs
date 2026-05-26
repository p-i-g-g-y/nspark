using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for reading the wallet's
/// balance and the underlying leaf inventory.
/// </summary>
public static class BalanceService
{
    /// <summary>
    /// Locked statuses that count toward <c>SatsBalance.Owned</c> but not
    /// toward <c>SatsBalance.Available</c>. Mirrors the Swift / Kotlin / TS
    /// Spark SDKs so balances agree across language clients.
    /// </summary>
    private static readonly HashSet<string> LockedStatuses = new(StringComparer.Ordinal)
    {
        "TRANSFER_LOCKED",
        "SPLIT_LOCKED",
        "AGGREGATE_LOCK",
        "RENEW_LOCKED",
    };

    /// <summary>
    /// Query the wallet's balance from the coordinator Signing Operator and
    /// aggregate it into <see cref="WalletBalance"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Performs two RPCs against the coordinator: <c>query_nodes</c> for the
    /// leaf inventory and <c>query_pending_transfers</c> for inbound
    /// transfers that have not yet been claimed. The three satoshi totals
    /// in <see cref="SatsBalance"/> are computed as follows:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Available</b>: sum of leaves with status <c>AVAILABLE</c>.</description></item>
    ///   <item><description><b>Owned</b>: <i>Available</i> + leaves in any of the locked statuses
    ///   (<c>TRANSFER_LOCKED</c>, <c>SPLIT_LOCKED</c>, <c>AGGREGATE_LOCK</c>, <c>RENEW_LOCKED</c>).</description></item>
    ///   <item><description><b>Incoming</b>: total value of pending inbound transfers + leaves
    ///   in the <c>CREATING</c> state (in-flight deposits).</description></item>
    /// </list>
    /// <para>
    /// Token balances are currently always an empty list; populated in a
    /// future release when <c>TokenService</c> lands. See
    /// <c>docs/configuration.md</c> for the migration notes.
    /// </para>
    /// </remarks>
    public static async Task<WalletBalance> GetBalanceAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet
            : Network.Regtest;

        var nodesResponse = await client.query_nodesAsync(
            new QueryNodesRequest
            {
                OwnerIdentityPubkey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        long available = 0;
        long owned = 0;
        long incomingFromCreating = 0;
        var leaves = new List<SparkLeaf>(nodesResponse.Nodes.Count);

        foreach (var kv in nodesResponse.Nodes)
        {
            var node = kv.Value;
            var value = (long)node.Value;

            if (node.Status == "AVAILABLE")
            {
                available += value;
                owned += value;
                leaves.Add(new SparkLeaf(
                    Id: kv.Key,
                    TreeId: node.TreeId,
                    ValueSats: value,
                    Status: node.Status)
                {
                    Node = node,
                });
            }
            else if (LockedStatuses.Contains(node.Status))
            {
                owned += value;
            }
            else if (node.Status == "CREATING")
            {
                incomingFromCreating += value;
            }
        }

        var pendingTransfers = await client.query_pending_transfersAsync(
            new TransferFilter
            {
                ReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        long incomingFromTransfers = 0;
        foreach (var transfer in pendingTransfers.Transfers)
        {
            incomingFromTransfers += (long)transfer.TotalValue;
        }

        var satsBalance = new SatsBalance(
            Available: available,
            Owned: owned,
            Incoming: incomingFromCreating + incomingFromTransfers);

        // Token balances are best-effort: if the token RPC fails we fall back
        // to an empty list so a single bad token-service round-trip doesn't
        // break the entire balance read. Callers that need failures surfaced
        // explicitly should call wallet.GetTokenBalancesAsync() directly.
        IReadOnlyList<TokenBalance> tokenBalances;
        try
        {
            tokenBalances = await wallet.GetTokenBalancesAsync(ct).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException)
        {
            tokenBalances = Array.Empty<TokenBalance>();
        }

        return new WalletBalance(satsBalance, tokenBalances, leaves);
    }

    /// <summary>
    /// Query all leaf nodes currently owned by the wallet that are in the
    /// <c>AVAILABLE</c> state.
    /// </summary>
    public static async Task<IReadOnlyList<SparkLeaf>> GetLeavesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet
            : Network.Regtest;

        var response = await client.query_nodesAsync(
            new QueryNodesRequest
            {
                OwnerIdentityPubkey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return response.Nodes
            .Where(kv => kv.Value.Status == "AVAILABLE")
            .Select(kv => new SparkLeaf(
                Id: kv.Key,
                TreeId: kv.Value.TreeId,
                ValueSats: (long)kv.Value.Value,
                Status: kv.Value.Status)
            {
                Node = kv.Value,
            })
            .ToList();
    }
}
