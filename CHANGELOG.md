# Changelog

All notable changes to **NSpark** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The public API contract is enforced by `PublicAPI.Shipped.txt` files in the
source tree; any change to that file is, by definition, a breaking change and
will be reflected here.

## [Unreleased]

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

## [0.1.0] - TBD

Initial public preview.

[Unreleased]: https://github.com/p-i-g-g-y/nspark/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/p-i-g-g-y/nspark/releases/tag/v0.1.0
