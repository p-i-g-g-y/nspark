# Architecture

This document explains how NSpark is layered, which types are public vs.
internal, what's shared across wallets vs. per-wallet, and how the SDK talks
to Spark Signing Operators (SOs) and the Spark Service Provider (SSP).

## High-level shape

```
┌─────────────────────────────────────────────────────────────────────┐
│                       Your application (.NET)                       │
└─────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│  SparkConnection (singleton)        ── shared transport + lifetime  │
│   ├─ HttpClient (from IHttpClientFactory)                           │
│   ├─ GrpcConnectionPool  (one channel per Signing Operator)         │
│   ├─ SparkAuthenticator  (token cache, TTL + LRU eviction)          │
│   ├─ ServerTimeSync                                                 │
│   └─ ILoggerFactory, ActivitySource, Meter, Polly pipeline          │
└─────────────────────────────────────────────────────────────────────┘
                                  │
                  CreateWallet(mnemonic | ISparkSigner)
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│  SparkWallet (per-wallet, lightweight)                              │
│   ├─ ISparkSigner   (BIP-39/32, ECDSA, FROST round delegation)      │
│   └─ SspGraphQLClient   (per-wallet auth token, shared HttpClient)  │
└─────────────────────────────────────────────────────────────────────┘
                                  │
              ┌───────────────────┼───────────────────┐
              ▼                   ▼                   ▼
       ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
       │ Signing Ops  │    │     SSP      │    │   Bitcoin    │
       │ (gRPC × N)   │    │  (GraphQL)   │    │  L1 chain    │
       └──────────────┘    └──────────────┘    └──────────────┘
```

## Lifetimes

| Object              | Lifetime                        | Why                                                              |
| ------------------- | ------------------------------- | ---------------------------------------------------------------- |
| `SparkConnection`   | Singleton per app               | Owns gRPC channels + auth-token cache; expensive to spin up.     |
| `SparkWallet`       | Cheap, create one per identity  | Just a tuple of (connection, signer, SSP client). No I/O on ctor. |
| `ISparkSigner`      | Lifetime of the wallet          | Holds the key material; replace via DI for HSM-backed wallets.   |
| gRPC channel        | Lifetime of `SparkConnection`   | HTTP/2 multiplexing — one channel per SO is enough.              |
| Auth tokens         | TTL-evicted in `SparkAuthenticator` | Bounded LRU cache (default 1024 entries).                    |

Register once, create many wallets:

```csharp
builder.Services.AddSpark(opts => opts.Network = SparkNetwork.Mainnet);
// builder.Services.AddHostedService<NSpark.Hosting.SparkHostedService>();  // optional
```

Then in any controller / handler:

```csharp
public sealed class Wallets(SparkConnection spark)
{
    public SparkWallet ForUser(string mnemonic) => spark.CreateWallet(mnemonic);
}
```

`SparkConnection` is `IAsyncDisposable`. The DI container disposes it on
shutdown; outside DI, wrap in `await using`.

## Public vs. internal surface

Everything you write code against lives in three namespaces:

- `NSpark` — `SparkConnection`, `SparkWallet`, `SparkOptions`, `SparkNetwork`,
  `SigningOperatorConfig`, `ServiceCollectionExtensions`.
- `NSpark.Models` — value records (`SparkLeaf`, `SatsBalance`, `WalletBalance`,
  `LightningInvoice`, `SparkTransfer`, `DepositAddress`, `TokenMetadata`,
  `TokenBalance`, `TokenOutputInfo`, `TokenCreationResult`,
  `TokenTransferResult`, `TokenSelectionStrategy`, `SparkEvent` and its
  cases, `WalletSettings`, `FeeQuote`, `DepositFeeEstimate`, `DepositUtxo`,
  `TransferPage`, `UnusedDepositAddress`).
- `NSpark.Services` — extension methods on `SparkWallet` for every domain
  operation (Lightning, Transfer, Deposit, Withdrawal, Claim, Swap, Token,
  Privacy, Event subscription, Bech32m / TokenIdentifier / SparkAddress
  helpers).
- `NSpark.Exceptions` — `SparkException` and its subclasses.
- `NSpark.Diagnostics` — `SparkActivitySource`, `SparkMeter`, `LogEvents`,
  `Sensitive<T>`.
- `NSpark.Connection` — `SparkResiliencePolicies` (Polly v8 pipeline) only.
  Everything else under that namespace is `internal`.
- `NSpark.Hosting` — `SparkHostedService`.

Generated protobuf types under `NSpark.Proto*` are deliberately `internal`.
The public API contract is tracked in `src/NSpark/PublicAPI.Shipped.txt` +
`PublicAPI.Unshipped.txt` and enforced by
`Microsoft.CodeAnalysis.PublicApiAnalyzers` at build time.

## Transport layers

### gRPC to Signing Operators

`GrpcConnectionPool` (internal) holds one `GrpcChannel` per configured SO.
HTTP/2 multiplexes — one channel handles every concurrent request for that
operator. Channels live as long as the `SparkConnection`.

Auth uses challenge-response: the SO emits a nonce, the wallet signs it with
its identity key, the SO returns a session token. Tokens are cached in
`SparkAuthenticator` (LRU + TTL — default 1024 entries, ~1 hour TTL minus
30s skew). The cache is per-`SparkConnection`, so different processes don't
share it.

### GraphQL to the SSP

`SspGraphQLClient` (internal) uses the shared `HttpClient` from
`IHttpClientFactory`. The SSP runs a separate auth flow (also
challenge-response, signed with the same identity key). A separate token
cache lives in `SspAuthenticator` (internal).

The SSP brokers off-Spark interactions:
- Lightning routing (in and out)
- Cooperative on-chain exits (withdrawals)
- Static-deposit claim quotes

## Resilience

Outbound gRPC calls flow through a Polly v8 pipeline assembled by
`SparkResiliencePolicies.Build()`. The pipeline:

1. **Timeout** — defaults to 30 seconds per attempt.
2. **Retry** — exponential backoff + jitter, default 3 attempts, only on
   transient `RpcException` status codes (`Unavailable`,
   `DeadlineExceeded`, `ResourceExhausted`, `Aborted`).

`Build(timeout, maxRetries)` exposes the knobs; pass `maxRetries: 0` to
disable retries entirely. See [`error-handling.md`](error-handling.md) for
the full retryability matrix and how `SparkConnectionException.IsRetryable`
maps to gRPC status codes.

## Observability

NSpark ships **two** OpenTelemetry sources, both named `"NSpark"`:

- `ActivitySource` — every public wallet operation emits a span (e.g.
  `nspark.lightning.invoice.create`, `nspark.transfer.send`, `nspark.frost.sign`).
- `Meter` — counters (`nspark.payments.completed`, `nspark.payments.failed`,
  `nspark.so_grpc.errors`, …) and histograms (`nspark.payment.duration`,
  `nspark.frost.sign.duration`, …).

No exporter is wired up by default; subscribe in your OTel builder. See
[`observability.md`](observability.md).

Logging uses `Microsoft.Extensions.Logging`. Every log entry carries a
documented `EventId` from `NSpark.Diagnostics.LogEvents`; see
[`logging.md`](logging.md) for the full table.

## FROST signing

The cryptographic heart of Spark is a 2-of-N FROST threshold signature
across the Signing Operators. The wallet:

1. Generates VSS shares of the secret material.
2. Encrypts each share to the corresponding SO's identity public key (ECIES).
3. Sends the encrypted shares + per-SO signing commitments to the
   coordinator SO.

The wallet-side crypto is delegated to a Rust library
(`spark_frost`, the same one the JS/Swift/Kotlin SDKs use) via UniFFI-
generated C# bindings (`SparkFrostBindings.cs`). The native binaries
ship inside the NuGet at `runtimes/<rid>/native/` for six RIDs. See
[`native-build.md`](native-build.md) if you want to rebuild from source.

## The two-claim model

This is the most important runtime contract to internalize, and it's
where most "why are my sats not showing up" questions come from.

**Receiving in Spark is a two-step process.** The sender produces a
transfer record on the Signing Operators; the receiver must **claim** it
before the value lands as a spendable `AVAILABLE` leaf.

The exception is on-chain deposits, which are claimed once their funding
transaction has enough confirmations. The pattern is:

```csharp
// After someone sends you sats:
//   1. their SendAsync / PayLightningInvoiceAsync returns
//   2. the SOs hold the transfer in a pending state for you
//   3. you must call ClaimPendingTransfersAsync to materialize it
var claimed = await wallet.ClaimPendingTransfersAsync(ct);
// `claimed` is the list of newly-claimed SparkTransfer records.
// Now GetBalanceAsync() reflects the new sats.
```

Long-running services that receive payments typically:

- Poll `ClaimPendingTransfersAsync` on an interval (e.g. every 5–10 s), or
- Subscribe to `SubscribeEventsAsync` and call claim on each
  `TransferReceivedEvent`.

The `Incoming` field of `SatsBalance` shows pending receivable sats that
haven't been claimed yet. See [`lightning/receiving-invoices.md`](lightning/receiving-invoices.md)
for the full receive flow with examples.

## Trust assumptions

NSpark trusts the host process, a majority of the configured Signing
Operators (currently 2 of 3 on mainnet), and the configured SSP. The full
threat model lives in [`trust-model.md`](trust-model.md) — required reading
before production deployments.

## Where to next

- [`getting-started.md`](getting-started.md) — install + wallet + first invoice
- [`configuration.md`](configuration.md) — every `SparkOptions` field
- [`signer.md`](signer.md) — custom `ISparkSigner` (HSM / KMS)
- [`error-handling.md`](error-handling.md) — what each exception means
- [`trust-model.md`](trust-model.md) — what NSpark defends against
