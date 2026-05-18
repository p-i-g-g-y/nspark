# Changelog

All notable changes to **NSpark** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The public API contract is enforced by `PublicAPI.Shipped.txt` files in the
source tree; any change to that file is, by definition, a breaking change and
will be reflected here.

## [Unreleased]

## [0.1.0-alpha.4] - 2026-05-17

### Fixed
- SSP-rejected refund-tx construction in five hot paths. The SSP validates
  every refund tx and rejects with `"expected value X on output 0"` when
  the refund output value isn't decremented by the standard Bitcoin-network
  fee (191 vbytes × 5 sat/vbyte = 955 sats). The previous code was passing
  `feeSats: 0` in:
  - `ClaimService.ClaimSingleTransferAsync` (incoming-transfer claim refund)
  - `LightningService.PayLightningInvoiceAsync` (v3 preimage-swap refund)
  - `SwapService.ProcessSwapBatchAsync` (swap-output refund)
  - `TransferService.SendAsync` (sender-side refund)
  - `WithdrawalService.WithdrawAsync` (cooperative-exit refund)

  All now use the single `SparkConstants.DefaultRefundFeeSats` constant.
  Verified end-to-end against the live SSP: Lightning send debits exactly
  `invoiceAmount + SSP routing fee` (no 955-sat surcharge to the user
  balance — the 955 is the on-chain miner fee that would only apply in a
  unilateral exit broadcast).

### Changed
- `DepositService.DefaultFeeSats` (and the equivalent local constant in
  `LightningService`) now point at `SparkConstants.DefaultRefundFeeSats`
  so all flows reference one source of truth.

### Added
- `NSpark.Services.SparkConstants` (internal) — centralizes the
  `DefaultRefundFeeSats = 191 × 5 = 955` constant. Internal-only, no
  public-API surface change.

## [0.1.0-alpha.3] - 2026-05-17

### Added
- `LightningService.GetLightningSendStatusAsync(SparkWallet, string, CancellationToken)`
  extension method — query the SSP for the status of an outgoing Lightning
  payment by its BOLT11 payment hash. Returns `null` when the SSP has no
  record, an in-flight `LightningSendStatus` (no fee / no preimage) while the
  HTLC is pending, and the final status with `FeeSats` + `Preimage` populated
  once the payment flips to `SUCCEEDED`.
- `NSpark.Models.LightningSendStatus` record carrying `PaymentHash`, `Status`,
  `FeeSats`, and `Preimage`.

### Fixed
- CS1573 doc-comment warning on `GetLightningSendStatusAsync`: the lone
  `<param>` tag triggered the "missing param tag" rule for the other
  parameters. Folded the `paymentHash` description into the `<summary>` to
  match the convention used by sibling extension methods in
  `LightningService`.

## [0.1.0-alpha.2] - 2026-05-12

### Added
- Full reference documentation under `docs/`:
  - `architecture.md` — layering, lifetimes, transport, observability,
    the two-claim model, trust assumptions.
  - `lightning/receiving-invoices.md` — full receive flow with the
    `ClaimPendingTransfersAsync` step called out as required.
  - `lightning/paying-invoices.md` — BOLT11 + Lightning Address paths,
    fee estimation, failure modes, idempotency.
  - `lightning/description-hash.md` — NIP-57 zap-receiver pattern
    with the two-claim flow honored.
  - `signer.md` — `ISparkSigner` contract, when to write a custom one
    (HSM / KMS / hardware), per-member implementation guide.
  - `deposits.md` — single-use and static deposit flows, including the
    two-claim sequence for static deposits
    (`ClaimStaticDepositAsync` then `ClaimPendingTransfersAsync`).
  - `withdrawals.md` — cooperative-exit flow, fee quote, idempotency
    notes.
  - `configuration.md` — every `SparkOptions` field documented.
  - `faq.md`, `glossary.md`.
- Suppressed `CA1873` repository-wide (joins `CA1848` — neither rule
  fits NSpark's pattern of one-shot lifecycle logs with trivial
  property-access arguments). Fixes the CI build on the latest
  NetAnalyzers shipped on GitHub's Windows/macOS runners.

### Fixed
- Documentation URLs throughout the codebase now point at the public
  repository `github.com/p-i-g-g-y/nspark` (no remaining references to
  the pre-public path).

## [0.1.0-alpha.1] - 2026-05-12

### Added
- Multi-targeting: `net8.0;net9.0;net10.0`.
- Central package management (`Directory.Packages.props`) and shared build
  infrastructure (`Directory.Build.props`, `.editorconfig`, `global.json`).
- Custom exception hierarchy rooted at `SparkException` with retryability
  classification (`SparkConnectionException.FromRpc`, `InvalidBolt11Exception`,
  `InsufficientFundsException`, `PaymentFailedException`,
  `InvoiceExpiredException`, etc.).
- Structured logging via `ILogger<T>` with documented `EventId`s in
  `NSpark.Diagnostics.LogEvents`.
- Sensitive-data redaction via `NSpark.Diagnostics.Sensitive<T>`.
- OpenTelemetry instrumentation: `ActivitySource("NSpark")` and
  `Meter("NSpark")` (`NSpark.Diagnostics.SparkActivitySource`,
  `NSpark.Diagnostics.SparkMeter`).
- Polly v8 resilience pipeline scaffolding
  (`NSpark.Connection.SparkResiliencePolicies`).
- Hosted-service hook for ASP.NET Core / generic host integrations
  (`NSpark.Hosting.SparkHostedService`).
- `IAsyncDisposable` on `SparkConnection`.

### Changed
- Renamed package, assembly, and namespace from `Spark.Client` to **`NSpark`**.
- Renamed `SparkClient` → `SparkConnection`,
  `SparkClientOptions` → `SparkOptions`,
  `AddSparkClient(...)` → `AddSpark(...)`.
- Auth token cache is now per-instance, TTL-evicted with an LRU size cap
  (default 1024 entries) — previously process-global and unbounded.
- gRPC-generated protobuf types are emitted as `internal`; transport-layer
  classes (`GrpcConnectionPool`, `SparkAuthenticator`, `ServerTimeSync`,
  `SspGraphQLClient`) demoted to `internal`. Public surface is now limited
  to `SparkConnection`, `SparkWallet`, `SparkOptions`, `ISparkSigner`,
  models, exceptions, diagnostics, hosting, and resilience helpers.

### Security
- `KeyDerivation.ComputePreimage` zeroes the local HTLC private-key buffer
  with `CryptographicOperations.ZeroMemory` after computing the preimage.
- See [docs/trust-model.md](docs/trust-model.md) for the documented threat
  model and the default Signing Operator / SSP trust assumptions.

[Unreleased]: https://github.com/p-i-g-g-y/nspark/compare/v0.1.0-alpha.2...HEAD
[0.1.0-alpha.2]: https://github.com/p-i-g-g-y/nspark/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/p-i-g-g-y/nspark/releases/tag/v0.1.0-alpha.1
