using Microsoft.Extensions.Logging;

namespace NSpark.Diagnostics;

/// <summary>
/// Canonical <see cref="EventId"/> registry for every log entry produced by the SDK.
/// Consumers can subscribe to specific IDs to alert on or suppress noisy log lines.
/// </summary>
/// <remarks>
/// ID ranges (mirrors <c>docs/logging.md</c>):
///   1000-1999: Lightning (invoice create/pay, BOLT decode)
///   2000-2999: Transfers / claims / FROST signing
///   3000-3999: Connection / authentication / SSP
///   4000-4999: Deposits / withdrawals / swaps
///   5000-5999: Wallet lifecycle, configuration, hosted service
///   9000-9999: Errors and fatal conditions.
/// New IDs MUST be appended; never reuse a number.
/// </remarks>
public static class LogEvents
{
    // ----- 1000-1999: Lightning -----
    /// <summary>Lightning invoice created on the receiver wallet.</summary>
    public static readonly EventId LightningInvoiceCreated = new(1001, nameof(LightningInvoiceCreated));

    /// <summary>Lightning send operation kicked off.</summary>
    public static readonly EventId LightningPaymentStarted = new(1010, nameof(LightningPaymentStarted));

    /// <summary>Lightning send operation completed successfully.</summary>
    public static readonly EventId LightningPaymentSucceeded = new(1011, nameof(LightningPaymentSucceeded));

    /// <summary>Lightning send operation failed.</summary>
    public static readonly EventId LightningPaymentFailed = new(1012, nameof(LightningPaymentFailed));

    /// <summary>BOLT11 payment request decoded.</summary>
    public static readonly EventId Bolt11Decoded = new(1020, nameof(Bolt11Decoded));

    // ----- 2000-2999: Transfers / Claims / FROST -----
    /// <summary>Spark transfer send initiated.</summary>
    public static readonly EventId TransferSendStarted = new(2001, nameof(TransferSendStarted));

    /// <summary>Spark transfer send completed.</summary>
    public static readonly EventId TransferSendCompleted = new(2002, nameof(TransferSendCompleted));

    /// <summary>Incoming transfer claimed.</summary>
    public static readonly EventId TransferClaimed = new(2010, nameof(TransferClaimed));

    /// <summary>FROST signing round completed.</summary>
    public static readonly EventId FrostSigningRoundCompleted = new(2020, nameof(FrostSigningRoundCompleted));

    // ----- 3000-3999: Connection / Auth / SSP -----
    /// <summary>gRPC channel to a Signing Operator established.</summary>
    public static readonly EventId GrpcChannelOpened = new(3001, nameof(GrpcChannelOpened));

    /// <summary>gRPC call failed at the transport level.</summary>
    public static readonly EventId GrpcCallFailed = new(3002, nameof(GrpcCallFailed));

    /// <summary>Auth token refreshed from a Signing Operator.</summary>
    public static readonly EventId AuthTokenRefreshed = new(3010, nameof(AuthTokenRefreshed));

    /// <summary>Auth token reused from cache (fast path).</summary>
    public static readonly EventId AuthTokenCacheHit = new(3011, nameof(AuthTokenCacheHit));

    /// <summary>SSP GraphQL request issued.</summary>
    public static readonly EventId SspGraphQLRequest = new(3020, nameof(SspGraphQLRequest));

    // ----- 4000-4999: Deposits / Withdrawals / Swaps -----
    /// <summary>Deposit address generated.</summary>
    public static readonly EventId DepositAddressGenerated = new(4001, nameof(DepositAddressGenerated));

    /// <summary>On-chain deposit claimed into the Spark wallet.</summary>
    public static readonly EventId DepositClaimed = new(4002, nameof(DepositClaimed));

    /// <summary>On-chain withdrawal initiated.</summary>
    public static readonly EventId WithdrawalInitiated = new(4010, nameof(WithdrawalInitiated));

    // ----- 5000-5999: Lifecycle -----
    /// <summary>SparkConnection (NSpark connection host) created and ready.</summary>
    public static readonly EventId ClientReady = new(5001, nameof(ClientReady));

    /// <summary>SparkConnection disposing — flushing channels and caches.</summary>
    public static readonly EventId ClientDisposing = new(5002, nameof(ClientDisposing));

    /// <summary>Hosted service started (background token refresh).</summary>
    public static readonly EventId HostedServiceStarted = new(5010, nameof(HostedServiceStarted));

    // ----- 9000-9999: Errors -----
    /// <summary>Unexpected error escaped an SDK boundary.</summary>
    public static readonly EventId UnhandledError = new(9001, nameof(UnhandledError));
}
