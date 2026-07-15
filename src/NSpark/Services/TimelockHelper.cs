using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>
/// Shared relative-timelock math for leaf spend paths. A leaf's timelock lives
/// in the low 16 bits of its refund transaction's first-input nSequence; bit 30
/// (the relative-timelock type flag) must be preserved across every decrement.
/// </summary>
internal static class TimelockHelper
{
    /// <summary>Blocks subtracted from the refund timelock on every spend hop.</summary>
    internal const uint TimeLockInterval = 100;

    /// <summary>The direct refund tx timelock sits this many blocks above the CPFP one.</summary>
    internal const uint DirectTimelockOffset = 50;

    /// <summary>
    /// Compute the next (cpfp, direct) sequences for a leaf spend by decrementing
    /// the refund timelock one interval. The timelock must be strictly greater
    /// than <see cref="TimeLockInterval"/> — the coordinator rejects a decrement
    /// that reaches zero, and unguarded uint subtraction would silently wrap.
    /// </summary>
    /// <exception cref="SparkLeafTimelockExhaustedException">
    /// The leaf's refund timelock is at the floor; it needs renewal before it can move.
    /// </exception>
    internal static (uint Cpfp, uint Direct) ComputeNextSequences(
        byte[] refundTxBytes, string operation, string? leafId = null)
    {
        var rawSequence = ClaimService.ParseInputSequence(refundTxBytes);
        var currentTimelock = rawSequence & 0xFFFF;
        var bit30 = rawSequence & (1u << 30);

        if (currentTimelock <= TimeLockInterval)
        {
            throw new SparkLeafTimelockExhaustedException(
                operation,
                $"Leaf timelock exhausted ({currentTimelock} <= {TimeLockInterval}); needs renewal before it can move.")
            {
                LeafId = leafId,
            };
        }

        var nextTimelock = currentTimelock - TimeLockInterval;
        return (bit30 | nextTimelock, bit30 | (nextTimelock + DirectTimelockOffset));
    }

    /// <summary>
    /// True when the refund timelock can still be decremented one interval —
    /// i.e. the leaf can join a transfer/swap without hitting the floor guard.
    /// </summary>
    internal static bool TimelockCanDecrement(byte[] refundTxBytes)
    {
        return (ClaimService.ParseInputSequence(refundTxBytes) & 0xFFFF) > TimeLockInterval;
    }
}
