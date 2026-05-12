using Grpc.Core;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace NSpark.Connection;

/// <summary>
/// Builders for the default Polly v8 resilience pipeline used when calling
/// Signing Operators. The pipeline composes a timeout, an exponential retry
/// (with jitter) for transient <see cref="RpcException"/> codes, and a
/// breaker that opens after sustained failures.
/// </summary>
/// <remarks>
/// Wired into <see cref="GrpcConnectionPool"/> by future iterations of the
/// SDK. Today it is exposed so consumers can pre-configure their own gRPC
/// channels with the same defaults, and so test scaffolding can inject a
/// no-op pipeline for fault injection.
/// </remarks>
public static class SparkResiliencePolicies
{
    /// <summary>Default per-call timeout for SO RPCs.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default maximum retry attempts on transient failures.</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>Build the standard NSpark gRPC resilience pipeline.</summary>
    public static ResiliencePipeline Build() => Build(DefaultTimeout, DefaultMaxRetries);

    /// <summary>
    /// Build a resilience pipeline with explicit parameters.
    /// </summary>
    /// <param name="timeout">Per-attempt timeout.</param>
    /// <param name="maxRetries">Maximum retry attempts (0 disables retry).</param>
    public static ResiliencePipeline Build(TimeSpan timeout, int maxRetries)
    {
        var builder = new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = timeout,
            });

        if (maxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(static rpc => IsTransient(rpc.StatusCode)),
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
            });
        }

        return builder.Build();
    }

    private static bool IsTransient(StatusCode statusCode) => statusCode switch
    {
        StatusCode.Unavailable => true,
        StatusCode.DeadlineExceeded => true,
        StatusCode.ResourceExhausted => true,
        StatusCode.Aborted => true,
        _ => false,
    };
}
