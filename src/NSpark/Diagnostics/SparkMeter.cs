using System.Diagnostics.Metrics;
using System.Reflection;

namespace NSpark.Diagnostics;

/// <summary>
/// Canonical <see cref="Meter"/> for the SDK. Subscribe to <c>"NSpark"</c> from an
/// OpenTelemetry meter provider to receive counters and histograms produced by NSpark.
/// </summary>
/// <remarks>
/// Naming follows OpenTelemetry semantic conventions where applicable.
/// All instruments are created lazily and reused.
/// </remarks>
public static class SparkMeter
{
    /// <summary>The meter name; subscribe to this in your meter provider.</summary>
    public const string Name = "NSpark";

    private static readonly string s_version =
        typeof(SparkMeter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SparkMeter).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>The shared <see cref="Meter"/> instance.</summary>
    public static readonly Meter Meter = new(Name, s_version);

    // --- Counters ---

    /// <summary>Number of Lightning payments completed successfully.</summary>
    public static readonly Counter<long> PaymentsCompleted =
        Meter.CreateCounter<long>("nspark.payments.completed", unit: "{payment}", description: "Lightning payments that completed successfully.");

    /// <summary>Number of Lightning payments that failed.</summary>
    public static readonly Counter<long> PaymentsFailed =
        Meter.CreateCounter<long>("nspark.payments.failed", unit: "{payment}", description: "Lightning payments that failed.");

    /// <summary>Number of gRPC errors received from Signing Operators.</summary>
    public static readonly Counter<long> SoGrpcErrors =
        Meter.CreateCounter<long>("nspark.so_grpc.errors", unit: "{error}", description: "gRPC errors received from Signing Operators, by status code.");

    /// <summary>Auth token cache hits.</summary>
    public static readonly Counter<long> AuthTokenCacheHits =
        Meter.CreateCounter<long>("nspark.auth.token_cache.hits", unit: "{hit}", description: "Auth token requests served from cache without re-authenticating.");

    /// <summary>Auth token cache misses (forced re-auth).</summary>
    public static readonly Counter<long> AuthTokenCacheMisses =
        Meter.CreateCounter<long>("nspark.auth.token_cache.misses", unit: "{miss}", description: "Auth token requests that required a fresh challenge round.");

    // --- Histograms ---

    /// <summary>End-to-end duration of a Lightning payment, in milliseconds.</summary>
    public static readonly Histogram<double> PaymentDurationMs =
        Meter.CreateHistogram<double>("nspark.payment.duration", unit: "ms", description: "End-to-end Lightning payment duration.");

    /// <summary>FROST signing round latency, in milliseconds.</summary>
    public static readonly Histogram<double> FrostSignDurationMs =
        Meter.CreateHistogram<double>("nspark.frost.sign.duration", unit: "ms", description: "FROST signing round latency.");

    /// <summary>Round-trip time for a single gRPC call to a Signing Operator.</summary>
    public static readonly Histogram<double> SoRttMs =
        Meter.CreateHistogram<double>("nspark.so.rtt", unit: "ms", description: "Round-trip time for a gRPC call to a Signing Operator.");
}
