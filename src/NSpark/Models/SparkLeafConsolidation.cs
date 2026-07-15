namespace NSpark.Models;

/// <summary>
/// Outcome of a leaf consolidation run. <see cref="FeeSats"/> is MEASURED
/// (total before minus total after) rather than assumed — SSP swaps are
/// requested with fee_sats 0 today, and this surfaces it if that ever changes.
/// </summary>
/// <param name="LeavesBefore">Leaf count before the run.</param>
/// <param name="LeavesAfter">Leaf count after the run.</param>
/// <param name="TotalSatsBefore">Total leaf value before the run, in satoshis.</param>
/// <param name="TotalSatsAfter">Total leaf value after the run, in satoshis.</param>
/// <param name="Rounds">SSP swap rounds executed.</param>
/// <param name="SkippedLeaves">
/// Leaves whose refund timelock is exhausted — they cannot move off-chain
/// until the operators renew them, so consolidation skips them. They stay
/// fully exitable via the recovery snapshot.
/// </param>
public sealed record SparkLeafConsolidation(
    int LeavesBefore,
    int LeavesAfter,
    long TotalSatsBefore,
    long TotalSatsAfter,
    int Rounds,
    int SkippedLeaves)
{
    /// <summary>Measured cost of the run: total sats before minus total sats after.</summary>
    public long FeeSats => TotalSatsBefore - TotalSatsAfter;
}
