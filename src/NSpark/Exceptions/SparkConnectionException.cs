using Grpc.Core;

namespace NSpark.Exceptions;

/// <summary>
/// A network-level failure when talking to a Signing Operator or the SSP.
/// Wraps the underlying transport exception so callers do not have to depend
/// on <c>Grpc.Core</c> types. Most variants are retryable.
/// </summary>
public sealed class SparkConnectionException : SparkException
{
    /// <summary>gRPC status code, when the failure originated from a gRPC call.</summary>
    public StatusCode? GrpcStatusCode { get; init; }

    /// <summary>The endpoint that failed, when known.</summary>
    public string? Endpoint { get; init; }

    /// <inheritdoc />
    public override bool IsRetryable => GrpcStatusCode is
        StatusCode.Unavailable or
        StatusCode.DeadlineExceeded or
        StatusCode.ResourceExhausted or
        StatusCode.Aborted;

    /// <inheritdoc />
    public SparkConnectionException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkConnectionException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }

    /// <summary>
    /// Construct from a <see cref="RpcException"/>, preserving the
    /// gRPC status for retry classification.
    /// </summary>
    public static SparkConnectionException FromRpc(string operation, string endpoint, RpcException rpc)
    {
        return new SparkConnectionException(
            operation,
            $"gRPC call to {endpoint} failed ({rpc.StatusCode}): {rpc.Status.Detail}",
            rpc)
        {
            GrpcStatusCode = rpc.StatusCode,
            Endpoint = endpoint,
        };
    }
}
