# Logging reference

NSpark emits structured logs via `Microsoft.Extensions.Logging.ILogger<T>`.
Every entry carries an `EventId` defined in
`NSpark.Diagnostics.LogEvents` so consumers can filter, route, or alert on
specific events without inspecting message strings.

## Wiring

`AddSpark(...)` ensures `AddLogging()` has been called on the service
collection. Plug in any `ILoggerProvider` (Serilog, NLog, console,
`AddSimpleConsole`, …) and you're done.

```csharp
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.IncludeScopes = true; });
builder.Services.AddSpark();
```

Enable scopes if your sink supports them — NSpark uses
`logger.BeginScope(...)` to attach `WalletId`, `OperationId`, and
correlation identifiers around each wallet operation.

## EventId ranges

| Range | Domain |
|---|---|
| 1000–1999 | Lightning (invoice create/pay, BOLT decode) |
| 2000–2999 | Transfers / claims / FROST signing |
| 3000–3999 | Connection / authentication / SSP |
| 4000–4999 | Deposits / withdrawals / swaps |
| 5000–5999 | Wallet lifecycle, configuration, hosted service |
| 9000–9999 | Errors and fatal conditions |

IDs are append-only — once an event is logged at a given ID, that ID is
reserved forever.

## Concrete events

| ID | Name | When |
|---|---|---|
| 1001 | `LightningInvoiceCreated` | A receiver wallet successfully issued a BOLT11 invoice. |
| 1010 | `LightningPaymentStarted` | A sender wallet started a payment attempt. |
| 1011 | `LightningPaymentSucceeded` | A sender wallet received the preimage. |
| 1012 | `LightningPaymentFailed` | A sender wallet's payment was rejected or timed out. |
| 1020 | `Bolt11Decoded` | An invoice was decoded (debug-only by default). |
| 2001 | `TransferSendStarted` | A Spark-to-Spark transfer was initiated. |
| 2002 | `TransferSendCompleted` | A Spark-to-Spark transfer finalized. |
| 2010 | `TransferClaimed` | An incoming transfer was claimed. |
| 2020 | `FrostSigningRoundCompleted` | A FROST round produced a signature. |
| 3001 | `GrpcChannelOpened` | A new gRPC channel to a Signing Operator was established. |
| 3002 | `GrpcCallFailed` | A gRPC call returned an error status. |
| 3010 | `AuthTokenRefreshed` | A fresh auth token was issued. |
| 3011 | `AuthTokenCacheHit` | An auth token was reused from cache (debug-only). |
| 3020 | `SspGraphQLRequest` | A GraphQL request was sent to the SSP. |
| 4001 | `DepositAddressGenerated` | A new on-chain deposit address was generated. |
| 4002 | `DepositClaimed` | An on-chain deposit was claimed. |
| 4010 | `WithdrawalInitiated` | A withdrawal was sent to the SSP. |
| 5001 | `ClientReady` | A `SparkConnection` finished initializing. |
| 5002 | `ClientDisposing` | A `SparkConnection` is shutting down. |
| 5010 | `HostedServiceStarted` | The optional `SparkHostedService` started. |
| 9001 | `UnhandledError` | An unexpected error escaped an SDK boundary. |

## Redaction

NSpark **never** logs:

- Mnemonics
- BIP-32 xprivs or raw private keys
- HTLC preimages
- FROST shares
- ECIES ciphertexts
- Bearer tokens

When a sensitive value cannot be omitted entirely from a structured field,
NSpark wraps it in `NSpark.Diagnostics.Sensitive<T>` whose `ToString()`
returns `"***"`. Helpers in `SensitiveFormat` produce length-only redactions
(`SensitiveFormat.Redact(span)` → `"<redacted N bytes>"`) and short
fingerprints (`SensitiveFormat.Fingerprint(span)` → `"<deadbeef…>"`) for
correlation use cases.

Custom log enrichers that walk the underlying value of `Sensitive<T>`
defeat the protection — review enrichers before deploying.

## Performance

Hot-path log calls use the source-generated logging pattern (see
`LoggerMessage.Define` and the `LoggerMessageAttribute` source generator)
to avoid allocations when the destination log level is disabled. Cold-path
events use the regular `ILogger` extension methods.
