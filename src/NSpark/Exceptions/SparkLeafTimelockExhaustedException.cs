namespace NSpark.Exceptions;

/// <summary>
/// A leaf's refund timelock has reached the floor (at or below the 100-block
/// decrement interval), so its refund transaction cannot be re-signed with a
/// lower timelock. The leaf is frozen for transfers, swaps, withdrawals and
/// Lightning sends until it is renewed — see
/// <c>RenewalService.RenewExhaustedLeavesAsync</c>. Not retryable as-is.
/// </summary>
public sealed class SparkLeafTimelockExhaustedException : SparkException
{
    /// <summary>Identifier of the exhausted leaf, when known.</summary>
    public string? LeafId { get; init; }

    /// <inheritdoc />
    public SparkLeafTimelockExhaustedException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkLeafTimelockExhaustedException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}
