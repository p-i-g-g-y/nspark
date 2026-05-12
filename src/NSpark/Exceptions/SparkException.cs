namespace NSpark.Exceptions;

/// <summary>
/// Base class for every exception thrown by the Spark / NSpark SDK.
/// Catch <see cref="SparkException"/> to handle any SDK-originated failure.
/// </summary>
/// <remarks>
/// All NSpark exceptions carry an <see cref="Operation"/> tag and an
/// optional <see cref="CorrelationId"/>. Structured loggers should propagate
/// these to telemetry sinks. Whether an exception is safe to retry is
/// documented per-subclass and summarized in <c>docs/error-handling.md</c>.
/// </remarks>
public abstract class SparkException : Exception
{
    /// <summary>
    /// Logical operation name that was in flight when the failure occurred
    /// (for example <c>"lightning.pay"</c> or <c>"transfer.send"</c>).
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Optional correlation identifier propagated from the originating
    /// activity scope, useful for matching logs/traces across services.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>True if the operation can be retried without side effects.</summary>
    public virtual bool IsRetryable => false;

    /// <inheritdoc />
    protected SparkException(string operation, string message)
        : base(message)
    {
        Operation = operation;
    }

    /// <inheritdoc />
    protected SparkException(string operation, string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = operation;
    }
}
