using System.Diagnostics;
using System.Reflection;

namespace NSpark.Diagnostics;

/// <summary>
/// Canonical <see cref="ActivitySource"/> for the SDK. Subscribe to <c>"NSpark"</c>
/// from an OpenTelemetry tracer to receive every span produced by NSpark.
/// </summary>
/// <remarks>
/// Span naming convention: <c>nspark.&lt;domain&gt;.&lt;operation&gt;</c>.
/// Common tag keys are exposed as constants on <see cref="Tags"/> so that
/// instrumentation stays consistent across the codebase.
/// </remarks>
public static class SparkActivitySource
{
    /// <summary>The activity source name; subscribe to this in your tracer provider.</summary>
    public const string Name = "NSpark";

    private static readonly string s_version =
        typeof(SparkActivitySource).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SparkActivitySource).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>The shared <see cref="ActivitySource"/> instance.</summary>
    public static readonly ActivitySource Source = new(Name, s_version);

    /// <summary>Start a client-kind span; returns <c>null</c> if no listener is registered.</summary>
    public static Activity? Start(string name, ActivityKind kind = ActivityKind.Client) =>
        Source.StartActivity(name, kind);

    /// <summary>Canonical tag keys for NSpark spans.</summary>
    public static class Tags
    {
        /// <summary>Wallet identity public key (hex), used as a low-cardinality wallet id.</summary>
        public const string WalletId = "nspark.wallet.id";

        /// <summary>Network name: <c>"mainnet"</c> or <c>"regtest"</c>.</summary>
        public const string Network = "nspark.network";

        /// <summary>Signing operator URL (sanitized — host only).</summary>
        public const string SoAddress = "nspark.so.address";

        /// <summary>Payment amount in satoshis.</summary>
        public const string PaymentAmountSats = "nspark.payment.amount_sats";

        /// <summary>BOLT11 payment hash (32-byte hex).</summary>
        public const string InvoicePaymentHash = "nspark.invoice.payment_hash";

        /// <summary>Spark transfer id.</summary>
        public const string TransferId = "nspark.transfer.id";

        /// <summary>SSP GraphQL operation name.</summary>
        public const string SspOperation = "nspark.ssp.operation";

        /// <summary>gRPC status code stringified (e.g. <c>"Unavailable"</c>).</summary>
        public const string GrpcStatus = "nspark.grpc.status";
    }

    /// <summary>Canonical span names.</summary>
    public static class Spans
    {
        /// <summary>Lightning invoice creation.</summary>
        public const string LightningInvoiceCreate = "nspark.lightning.invoice.create";

        /// <summary>Lightning invoice payment.</summary>
        public const string LightningInvoicePay = "nspark.lightning.invoice.pay";

        /// <summary>Spark transfer send (outgoing).</summary>
        public const string TransferSend = "nspark.transfer.send";

        /// <summary>Spark transfer claim (incoming).</summary>
        public const string TransferClaim = "nspark.transfer.claim";

        /// <summary>On-chain deposit claim.</summary>
        public const string DepositClaim = "nspark.deposit.claim";

        /// <summary>On-chain withdrawal.</summary>
        public const string WithdrawalInitiate = "nspark.withdrawal.initiate";

        /// <summary>FROST signing round.</summary>
        public const string FrostSign = "nspark.frost.sign";

        /// <summary>Single gRPC call to a Signing Operator.</summary>
        public const string SoGrpcCall = "nspark.so.grpc.call";

        /// <summary>Single GraphQL call to the SSP.</summary>
        public const string SspGraphQLCall = "nspark.ssp.graphql.call";

        /// <summary>Signing Operator authentication round.</summary>
        public const string SoAuthenticate = "nspark.so.authenticate";
    }
}
