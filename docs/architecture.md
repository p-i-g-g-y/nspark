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
             await CreateWalletAsync(mnemonic | ISparkSigner)
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│  SparkWallet (per-wallet, lightweight)                              │
│   ├─ ISparkSigner   (async: identity, FROST, ECIES, tweak batches)  │
│   ├─ Cached IdentityPublicKey + DepositPublicKey (sync accessors)   │
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
| `SparkWallet`       | Cheap, create one per identity  | Construction performs one round-trip to the signer to cache identity + deposit pubkeys, then no I/O until you call an operation. |
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
    public Task<SparkWallet> ForUserAsync(string mnemonic, CancellationToken ct = default)
        => spark.CreateWalletAsync(mnemonic, ct: ct);
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

## FROST signing & the signer boundary

The cryptographic heart of Spark is a 2-of-N FROST threshold signature
across the Signing Operators. NSpark routes **every** private-key
operation through `ISparkSigner` (see [`signer.md`](signer.md)):

- Per-leaf FROST signing (`SignLeafFrostAsync` one-shot, or
  `GenerateLeafFrostNonceAsync` + `SignLeafFrostWithNonceAsync` two-phase).
- Per-leaf tweak shares (`BuildEncryptedSendTweaksAsync`,
  `BuildEncryptedClaimTweaksAsync`) — the signer generates the VSS shares
  *and* ECIES-encrypts them per-SO inside its trust boundary; the wallet
  receives only opaque encrypted blobs.
- Lightning preimage shares (`BuildEncryptedPreimageSharesAsync`) — same
  pattern, encrypted per-SO inside the signer.
- ECDSA over the identity key (`SignWithIdentityKeyAsync`,
  `SignCompactWithIdentityKeyAsync`) and ECIES decryption
  (`DecryptEciesWithIdentityKeyAsync`).

The default `SparkSigner` implements all of this in-process via NBitcoin
(BIP-39/32 key derivation) and the native `spark_frost` Rust library
(same one the JS/Swift/Kotlin SDKs use) through UniFFI-generated C#
bindings. The native binaries ship inside the NuGet at
`runtimes/<rid>/native/` for six RIDs — see [`native-build.md`](native-build.md)
if you want to rebuild from source.

Two architectural invariants enforced by file layout:

- `using uniffi.spark_frost;` appears in exactly three files —
  `Signer/SparkSigner.cs` (the default in-process signer),
  `Signer/FrostAggregator.cs` (public-only FROST aggregation wrapper),
  and `Services/SparkTxBuilder.cs` (public-only Bitcoin tx
  construction wrapper). Service code is uniffi-free.
- No plaintext share material, intermediate signing key, tweak signature
  payload, or random preimage ever crosses the wallet's address space
  in the encrypted-batch flows. Custom HSM/KMS-backed `ISparkSigner`
  implementations can run with zero plaintext private material in the
  wallet process.

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
