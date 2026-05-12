using System.ComponentModel;

namespace NSpark.Models;

/// <summary>
/// Full balance snapshot for a Spark wallet: sats breakdown by status,
/// token balances per token type, and the underlying leaves.
/// </summary>
/// <param name="SatsBalance">Available / owned / incoming satoshi totals.</param>
/// <param name="TokenBalances">Per-token-type holdings.</param>
/// <param name="Leaves">The leaves backing the available satoshi balance.</param>
public sealed record WalletBalance(
    SatsBalance SatsBalance,
    IReadOnlyList<TokenBalance> TokenBalances,
    IReadOnlyList<SparkLeaf> Leaves)
{
    /// <summary>
    /// Total satoshis the wallet owns — alias for <c>SatsBalance.Owned</c>,
    /// retained for backwards compatibility with v0.0.x previews.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Use SatsBalance.Available, SatsBalance.Owned, or SatsBalance.Incoming. Will be removed in v0.2.0.")]
    public long TotalSats => SatsBalance.Owned;
}
