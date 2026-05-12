# Observability

NSpark exposes an `ActivitySource` and a `Meter`, both named `"NSpark"`.
Subscribe to them from any OpenTelemetry tracer / meter provider — NSpark
ships **no** default exporter, so spans and metrics stay inside your
process until you explicitly forward them.

## Tracing

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;

var tracer = Sdk.CreateTracerProviderBuilder()
    .AddSource("NSpark")            // subscribe to NSpark spans
    .AddOtlpExporter()              // send to your collector
    .Build();
```

### Span names

| Span | Kind | Emitted from |
|---|---|---|
| `nspark.lightning.invoice.create` | Client | Invoice creation flow |
| `nspark.lightning.invoice.pay`    | Client | Pay flow |
| `nspark.transfer.send`            | Client | Outgoing Spark transfer |
| `nspark.transfer.claim`           | Client | Incoming Spark transfer claim |
| `nspark.deposit.claim`            | Client | On-chain deposit claim |
| `nspark.withdrawal.initiate`      | Client | On-chain withdrawal |
| `nspark.frost.sign`               | Internal | FROST signing round |
| `nspark.so.grpc.call`             | Client | gRPC call to a Signing Operator |
| `nspark.so.authenticate`          | Client | Auth challenge-response round |
| `nspark.ssp.graphql.call`         | Client | GraphQL call to the SSP |

### Canonical tags

`NSpark.Diagnostics.SparkActivitySource.Tags` exposes these as constants:

| Key | Description |
|---|---|
| `nspark.wallet.id` | Wallet identity public key (low-cardinality wallet id) |
| `nspark.network`   | `"mainnet"` or `"regtest"` |
| `nspark.so.address` | Signing operator URL |
| `nspark.payment.amount_sats` | Payment amount, satoshis |
| `nspark.invoice.payment_hash` | BOLT11 payment hash, 64-hex |
| `nspark.transfer.id` | Spark transfer id |
| `nspark.ssp.operation` | GraphQL operation name |
| `nspark.grpc.status` | gRPC status code string when an error occurred |

## Metrics

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;

var meter = Sdk.CreateMeterProviderBuilder()
    .AddMeter("NSpark")
    .AddOtlpExporter()
    .Build();
```

### Counters

| Instrument | Unit | Description |
|---|---|---|
| `nspark.payments.completed` | `{payment}` | Lightning payments that completed successfully |
| `nspark.payments.failed`    | `{payment}` | Lightning payments that failed |
| `nspark.so_grpc.errors`     | `{error}`   | gRPC errors received from Signing Operators |
| `nspark.auth.token_cache.hits` | `{hit}`  | Auth requests served from cache |
| `nspark.auth.token_cache.misses` | `{miss}` | Auth requests requiring re-authentication |

### Histograms

| Instrument | Unit | Description |
|---|---|---|
| `nspark.payment.duration`     | `ms` | End-to-end Lightning payment duration |
| `nspark.frost.sign.duration`  | `ms` | FROST signing round latency |
| `nspark.so.rtt`               | `ms` | Round-trip time to a Signing Operator |

## Correlating logs + traces + metrics

When `OpenTelemetry.Extensions.Hosting` is wired up, the active `Activity`
flows into `ILogger` scopes automatically. The result: every NSpark log
line in a span includes `TraceId` / `SpanId` for correlation in tools like
Grafana Tempo, Honeycomb, and Datadog.

For metrics ↔ traces correlation, attach the wallet id and the SO address
to both spans (`SparkActivitySource.Tags.*`) and metric attributes
(passed to `Counter<T>.Add(...)` / `Histogram<T>.Record(...)`).

## Privacy notes

The default exporters mentioned above send data *somewhere* — usually a
collector you own. Choose carefully:

- `nspark.invoice.payment_hash` is a public identifier; safe to export.
- `nspark.wallet.id` is the identity public key; it correlates wallet
  activity across spans. Treat it like an account identifier.
- `nspark.payment.amount_sats` reveals payment value; sensitive in some
  deployments. Drop or bucket it at the collector if needed.
- HTLC preimages, signing shares, and bearer tokens are **never** present
  on spans or metrics.
