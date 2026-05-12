# Configuration reference

Everything NSpark exposes for configuration lives on `SparkOptions`.
This page documents every field, what the default does, and when you'd
override it.

## The full surface

```csharp
public sealed class SparkOptions
{
    public SparkNetwork Network { get; set; } = SparkNetwork.Mainnet;

    public SigningOperatorConfig[] SigningOperators { get; set; }
        = GetDefaultOperators(SparkNetwork.Mainnet);

    public string[] SigningOperatorAddresses => /* derived */;

    public string SspUrl { get; set; }
        = "https://api.lightspark.com/graphql/spark/2025-03-19";

    public string SspIdentityPublicKeyHex { get; set; }
        = GetSspIdentityPublicKey(SparkNetwork.Mainnet);

    public static string GetSspIdentityPublicKey(SparkNetwork network);
    public static SigningOperatorConfig[] GetDefaultOperators(SparkNetwork network);
}

public sealed record SigningOperatorConfig(
    string Address,
    string Identifier,
    string IdentityPublicKeyHex);
```

## Wiring it up

### Direct construction

```csharp
using Microsoft.Extensions.Options;
using NSpark;

var options = Options.Create(new SparkOptions
{
    Network = SparkNetwork.Mainnet,
});

using var http = new HttpClient();
await using var spark = new SparkConnection(options, http);
```

### Via DI (`Microsoft.Extensions.DependencyInjection`)

```csharp
builder.Services.AddSpark(options =>
{
    options.Network = SparkNetwork.Mainnet;
});
```

### Bound from `IConfiguration`

```jsonc
// appsettings.json
{
  "Spark": {
    "Network": "Mainnet",
    "SspUrl": "https://api.lightspark.com/graphql/spark/2025-03-19"
  }
}
```

```csharp
builder.Services
    .AddOptions<SparkOptions>()
    .Bind(builder.Configuration.GetSection("Spark"))
    .ValidateOnStart();

builder.Services.AddSpark();   // uses the bound options
```

`SigningOperators` is a complex object — bind it explicitly or set it in
the `AddSpark` configuration lambda.

## Field-by-field

### `Network`

- Type: `SparkNetwork` (`Mainnet` | `Regtest`)
- Default: `Mainnet`
- Purpose: chooses which Bitcoin network the wallet operates on.

Determines:

- The HRP of Spark addresses (`spark1...` mainnet, `sparkrt1...` regtest)
  and token identifiers (`btkn1...` / `btknrt1...`).
- The BIP-32 derivation account number used by `SparkSigner.FromMnemonic`
  if the caller doesn't pass `account` explicitly (regtest → account 0,
  mainnet → account 1).
- The proto `Network` enum on every SO request.

**Setting `Network` alone does not auto-switch `SigningOperators` or
`SspUrl` / `SspIdentityPublicKeyHex`.** Each property defaults
independently. To use the regtest defaults across the board:

```csharp
var network = SparkNetwork.Regtest;
var options = Options.Create(new SparkOptions
{
    Network = network,
    SigningOperators = SparkOptions.GetDefaultOperators(network),
    SspIdentityPublicKeyHex = SparkOptions.GetSspIdentityPublicKey(network),
    SspUrl = "http://localhost:8080/graphql/spark",  // your regtest SSP
});
```

### `SigningOperators`

- Type: `SigningOperatorConfig[]`
- Default (mainnet, 3 operators):

| Identifier | Address                                    | Identity public key                                                  |
| ---------- | ------------------------------------------ | -------------------------------------------------------------------- |
| `…001`     | `https://0.spark.lightspark.com`           | `03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763` |
| `…002`     | `https://spark-operator.breez.technology`  | `03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77` |
| `…003`     | `https://2.spark.flashnet.xyz`             | `022eda13465a59205413086130a65dc0ed1b8f8e51937043161f8be0c369b1a410` |

The trust model documents these as **trusted by configuration** — they
are the production Spark operators. See
[`trust-model.md`](trust-model.md) for the threat-model implications of
keeping the default vs. overriding.

Each `SigningOperatorConfig` has three fields:

- `Address` — the operator's HTTPS endpoint (gRPC over HTTP/2).
- `Identifier` — 32-byte hex identifier used as the SO map key in
  multi-operator coordination messages. Must be unique within the array.
- `IdentityPublicKeyHex` — operator's secp256k1 identity public key,
  used for ECIES encryption of FROST shares destined for that operator
  and for verifying operator-signed messages.

### `SigningOperatorAddresses`

- Type: `string[]` (read-only, derived from `SigningOperators`)

Convenience accessor used internally by `GrpcConnectionPool`. You don't
set this — set `SigningOperators` instead.

### `SspUrl`

- Type: `string`
- Default: `https://api.lightspark.com/graphql/spark/2025-03-19`
- Purpose: GraphQL endpoint of the Spark Service Provider.

The SSP brokers Lightning routing (both directions), cooperative exits
(withdrawals), and static-deposit claims. Replace this if you run your
own SSP or target a different Spark deployment.

The path component `/2025-03-19` is the schema version pin. NSpark's
GraphQL queries were written against that schema; updating the URL to a
newer version is a breaking-change moment — be sure your NSpark version
is compatible.

### `SspIdentityPublicKeyHex`

- Type: `string` (hex)
- Default (mainnet): `023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93`
- Default (regtest): `022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe`
- Purpose: identity public key of the SSP, used as the HTLC hashlock
  destination and the receiver identity in Lightning swap flows.

If you change `SspUrl` to a custom SSP, **also** change
`SspIdentityPublicKeyHex` to that SSP's identity key. The two are paired
— pointing at a different SSP with the wrong identity key causes every
Lightning send to fail.

## Static helpers

### `SparkOptions.GetDefaultOperators(network)`

Returns the canonical operator array for the named network. Useful when
overriding `Network` after construction:

```csharp
var opts = new SparkOptions { Network = SparkNetwork.Regtest };
opts.SigningOperators = SparkOptions.GetDefaultOperators(opts.Network);
```

### `SparkOptions.GetSspIdentityPublicKey(network)`

Returns the canonical SSP identity public key for the named network.
Used in the same pattern as above.

## Per-wallet vs. per-connection

`SparkOptions` is **per `SparkConnection`** — one configuration for the
whole singleton. There's no `SparkOptions` on `SparkWallet`. The
implication: if your application talks to two networks (e.g. mainnet
and regtest simultaneously), spin up two `SparkConnection` instances
with two separate `IOptions<SparkOptions>` registrations.

Two `SparkConnection`s in one DI container is unusual but supported:

```csharp
builder.Services.AddKeyedSingleton<SparkConnection>("mainnet", (sp, _) =>
{
    var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("spark-mainnet");
    var lf = sp.GetRequiredService<ILoggerFactory>();
    return new SparkConnection(options, http, lf);
});
builder.Services.AddKeyedSingleton<SparkConnection>("regtest", /* … */);
```

## What you don't configure (yet)

These knobs aren't on `SparkOptions` in v0.1.x:

- **Token cache size / TTL**. `SparkAuthenticator` accepts these via
  constructor, but `SparkConnection` always uses the defaults
  (1024 entries / 1-minute refresh buffer). File an issue if you need
  them surfaced.
- **Polly pipeline parameters**. Use `SparkResiliencePolicies.Build()`
  to construct a pipeline; it isn't wired into outbound gRPC calls by
  default — file an issue.
- **gRPC channel options** (deadlines, max message size). Defaults from
  `Grpc.Net.Client` apply.
- **HttpClient settings**. Drive these via `IHttpClientFactory` named
  clients in your DI container.

## See also

- [`getting-started.md`](getting-started.md) — minimum setup.
- [`trust-model.md`](trust-model.md) — what the defaults imply for
  trust.
- [`logging.md`](logging.md) — how to attach an `ILoggerProvider`.
- [`observability.md`](observability.md) — how to plug in OTel.
