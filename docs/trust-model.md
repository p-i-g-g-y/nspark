# NSpark trust model

This document is the canonical statement of *what NSpark does and does not
defend against*. Read it before deploying NSpark in production. If your
threat model contradicts the assumptions below, NSpark may not be the right
SDK for your application.

## TL;DR

NSpark is a **non-custodial** Lightning SDK that trusts:

1. The **host process** to be honest (memory, files, network).
2. A **majority** of the configured Signing Operators (SOs) to be honest.
3. The **configured SSP** to be honest-but-curious (i.e. routes payments
   correctly but may attempt to learn the wallet's metadata).
4. The host's **clock** to be within reasonable tolerance of network time.

NSpark does **not** custody funds; keys are derived locally from a BIP-39
mnemonic (or a custom `ISparkSigner`) and only signatures and ECIES-
encrypted shares leave the process.

## Default endpoints

The default `SparkOptions` ship with hardcoded mainnet endpoints. These are
**trusted by configuration**. Override them via `SparkOptions.SigningOperators`
and `SparkOptions.SspUrl` if your deployment requires different operators.

### Default Signing Operators (mainnet)

| Identifier | Address | Identity public key |
|---|---|---|
| `…001` | `https://0.spark.lightspark.com` | `03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763` |
| `…002` | `https://spark-operator.breez.technology` | `03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77` |
| `…003` | `https://2.spark.flashnet.xyz` | `022eda13465a59205413086130a65dc0ed1b8f8e51937043161f8be0c369b1a410` |

The 2-of-3 threshold means **any single rogue operator cannot extract funds
or signatures**, but two colluding operators can. Take this into account
when deciding whether to override the defaults.

### Default Spark Service Provider (mainnet)

| Endpoint | Identity public key |
|---|---|
| `https://api.lightspark.com/graphql/spark/2025-03-19` | `023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93` |

The SSP routes outbound Lightning payments and brokers preimage swaps for
inbound payments. It **does not custody funds** — every payment is HTLC-
secured — but it can:

- Refuse to forward a payment (denial of service).
- Charge above-market fees (mitigated by `maxFeeSats`).
- Observe payment metadata (amount, destination node, timing).

## What NSpark defends against

| Threat | Defense |
|---|---|
| Single rogue Signing Operator | FROST 2-of-3 threshold signing; no individual SO holds the full key. |
| Single rogue SSP | HTLC payment flow; preimage release is atomic with payment. |
| Tampered description in BOLT11 | Description hash (32-byte SHA-256) is verified against the issued invoice. |
| Token replay against SO | Challenge-response auth with per-call signed challenge; tokens are TTL-bounded. |
| Unbounded auth-token retention | Per-instance LRU cache with TTL + size cap. |
| Routing-fee griefing | `maxFeeSats` enforced before HTLC construction. |
| Truncated reads of native binaries | SHA-256 of every shipped `libspark_frost` published in `runtimes/SHA256SUMS.txt` and the GitHub release notes. |
| Tampered NuGet package | SLSA build provenance attestation (verify with `gh attestation verify`). NuGet author signing will land once OrkLabs acquires a code-signing certificate. |

## What NSpark does **not** defend against

| Threat | Recommendation |
|---|---|
| **Host process compromise** (memory dump, ptrace, etc.) | NSpark assumes the host is trusted. Sensitive material is cleared best-effort via `CryptographicOperations.ZeroMemory` where the SDK owns the buffer, but the GC can move arrays and NBitcoin retains key state. Use process isolation (containers, dedicated VMs) for sensitive deployments. |
| **Mnemonic theft from disk / env** | NSpark accepts a `string mnemonic`. Loading it from a sealed secret store and avoiding string interning is the consumer's responsibility. For high-assurance setups, implement `ISparkSigner` directly against a hardware wallet or HSM and never let the mnemonic enter NSpark. |
| **Compromised user-supplied signer** | A faulty/malicious `ISparkSigner` implementation can produce invalid signatures or leak private keys. Audit any custom signer carefully. |
| **Colluding majority of SOs** | Out of scope. Choose operators you trust collectively. |
| **Quantum-capable adversary** | The protocol uses secp256k1; whole-network upgrade required. |
| **Side-channel attacks on the signing host** | Out of scope. Use HSMs for high-value workloads. |
| **Pre-image leakage via logs** | Mitigated by `Sensitive<T>` and the redaction conventions in `docs/logging.md`. Custom loggers that ignore those conventions can still leak — review your logger configuration. |
| **Bad-rng signer implementations** | NSpark inherits the entropy source from the underlying signer (NBitcoin's `RandomNumberGenerator` for the default `SparkSigner`). Ensure the platform's CSPRNG is healthy. |

## Operational recommendations

- **Pin dependencies**. Use `<PackageVersion>` (we already do via CPM) and a
  committed lock file. Run `dotnet list package --vulnerable --include-transitive`
  in CI.
- **Verify the NuGet** before installing in a production environment:
  ```bash
  dotnet nuget verify NSpark.<version>.nupkg
  gh attestation verify NSpark.<version>.nupkg --owner p-i-g-g-y
  ```
- **Limit log sinks**. Send structured logs to a sink that you control;
  redaction works only against `Sensitive<T>.ToString()`, not against
  consumers who log raw byte arrays.
- **Cap routing fees**. Always pass `maxFeeSats` in production. The
  default is permissive to keep examples short.
- **Run multiple wallets out of one `SparkConnection`** for tenant
  isolation — it's cheap (the connection pool is shared) and limits
  blast radius from misconfiguration.

## Reporting trust-boundary concerns

If you believe NSpark's behavior contradicts this document, treat it as a
security bug and follow [`SECURITY.md`](../SECURITY.md). If you believe the
document itself is wrong or incomplete, open a regular issue or PR — the
trust model evolves with the code.
