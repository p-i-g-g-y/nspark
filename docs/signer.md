# Custom signers (HSM, KMS, hardware wallets)

NSpark separates **what to sign** from **how to sign**. Every wallet
operation goes through an `ISparkSigner`, and the default
`SparkSigner` is a BIP-39 mnemonic-backed implementation built on
NBitcoin. If your application's key custody is outside the process
(HSM, AWS KMS, Azure Key Vault, hardware wallet, remote signer
service), implement `ISparkSigner` directly and never let a mnemonic
near NSpark.

## The contract

```csharp
namespace NSpark.Signer;

public interface ISparkSigner
{
    byte[] IdentityPublicKey { get; }       // 33-byte compressed secp256k1
    byte[] IdentityPrivateKey { get; }      // 32-byte scalar (used for ECIES)
    byte[] DepositPublicKey { get; }        // 33-byte compressed secp256k1

    byte[] SignWithIdentityKey(byte[] messageHash);          // DER signature
    byte[] SignCompactWithIdentityKey(byte[] messageHash);   // 64-byte r||s

    byte[] DeriveLeafSigningKey(string leafId);   // per-leaf private key
    byte[] DeriveStaticDepositKey(int index);     // per-index private key

    byte[] GeneratePreimage(string transferId);   // deterministic HMAC

    byte[] FrostSign(
        byte[] message, byte[] signingKey,
        byte[] groupKey, byte[] commitmentSeed);

    (byte[] hiding, byte[] binding) GenerateFrostCommitments(byte[] signingKey);
}
```

Every member returns raw bytes — no NBitcoin or BouncyCastle types leak
through the interface — so a remote signer can implement it with any
crypto backend.

## When to implement a custom signer

| Scenario                                                 | Recommendation                       |
| -------------------------------------------------------- | ------------------------------------ |
| Single-user desktop wallet, mnemonic loaded from disk    | Default `SparkSigner.FromMnemonic`   |
| Server-side wallet, mnemonic in secret manager           | Default, loaded once at startup       |
| Multi-tenant service, one wallet per user                | Default, one signer per request      |
| Hardware wallet (Ledger, BitBox, etc.)                   | **Custom `ISparkSigner`**            |
| HSM / KMS-backed enterprise wallet                       | **Custom `ISparkSigner`**            |
| You never want the private key in process memory         | **Custom `ISparkSigner`**            |

## How to plug a custom signer in

```csharp
using NSpark;
using NSpark.Signer;

public sealed class HsmSparkSigner : ISparkSigner
{
    // ... implement every member by delegating to your HSM ...
}

// Use it:
var signer = new HsmSparkSigner(/* HSM session */);
SparkWallet wallet = spark.CreateWallet(signer);  // bypasses mnemonic
```

`CreateWallet(ISparkSigner)` is the API entry-point that accepts any
implementation. `SparkConnection` itself is unchanged — the same
singleton handles HSM-backed wallets and mnemonic-backed wallets side
by side.

## Per-member implementation guide

### `IdentityPublicKey` and `IdentityPrivateKey`

The wallet's BIP-44-style identity key. NSpark uses the **public** half
constantly: every SO authentication, every transfer build, every Spark
address. The **private** half is used directly for:

- ECDSA signing of SO/SSP challenge bytes (`SignWithIdentityKey`,
  `SignCompactWithIdentityKey`).
- ECIES decryption when the SO returns encrypted shares back to the
  wallet (token mint flows).
- The HMAC key for `GeneratePreimage` (Lightning receive preimages).

For an HSM-backed signer, exposing `IdentityPrivateKey` as a real scalar
is the awkward part — the only required consumers are ECIES decryption
and the HMAC for preimages. Two patterns:

- **Acceptable**: Keep the identity key in software (it controls
  Lightning preimages anyway), put **only the leaf signing keys** in the
  HSM.
- **Strict**: Implement ECIES decryption and HMAC inside the HSM
  itself; have `IdentityPrivateKey` throw `NotSupportedException` and
  override the consumer paths (more invasive — file an issue if you
  need this).

### `DepositPublicKey`

Used as the user-side spending key inside the FROST-shared deposit
tree. The corresponding private key never needs to sign anything at the
client; it just contributes to the deposit address derivation.

### `SignWithIdentityKey` / `SignCompactWithIdentityKey`

ECDSA over secp256k1 with SHA-256 as the message hash function. The
input is **already** the 32-byte hash — your signer does **not** hash
again. The DER form is used for SO/SSP authentication; the compact
(64-byte `r || s`) form is used for leaf key-tweak signatures in
transfer/withdrawal flows.

### `DeriveLeafSigningKey(leafId)`

Returns the 32-byte private scalar that signs per-leaf FROST messages.
The default derives this via BIP-32 hardened derivation:
`m/8797555' / account' / 1' / (SHA256(leafId)[0:4] % 2^31 + 2^31)`.
A custom signer can pick any deterministic scheme as long as the same
leaf id always maps to the same key.

### `DeriveStaticDepositKey(index)`

Same shape, different parent (BIP-44 child index 3 instead of 1).

### `GeneratePreimage(transferId)`

A 32-byte preimage that **must be deterministic** for the same
`transferId` — Lightning HTLC settlement looks up by payment hash, so
re-deriving the same preimage from the same transfer id is the only way
to recover after a crash. The default uses `HMAC-SHA256(htlcPreimageKey,
UTF8(transferId))`.

### `FrostSign` and `GenerateFrostCommitments`

These delegate to the native `spark_frost` Rust library via UniFFI
bindings — the same library every Spark SDK uses. A custom signer can
delegate to the same native library (the bindings are public in
`uniffi.spark_frost` namespace) or re-implement FROST round signing
against its own crypto backend (much more work; not recommended).

## Reference: minimal signer that proxies a remote service

```csharp
public sealed class RemoteSparkSigner : ISparkSigner
{
    private readonly IRemoteSigner _remote;
    private byte[]? _identityPubKey;
    private byte[]? _depositPubKey;

    public RemoteSparkSigner(IRemoteSigner remote) => _remote = remote;

    public byte[] IdentityPublicKey
        => _identityPubKey ??= _remote.GetPublicKey("identity").Result;

    public byte[] DepositPublicKey
        => _depositPubKey ??= _remote.GetPublicKey("deposit").Result;

    public byte[] IdentityPrivateKey
        => throw new NotSupportedException(
            "Identity private key is not exposed; ECIES happens in the remote signer.");

    public byte[] SignWithIdentityKey(byte[] messageHash)
        => _remote.Sign("identity", messageHash, format: "der").Result;

    public byte[] SignCompactWithIdentityKey(byte[] messageHash)
        => _remote.Sign("identity", messageHash, format: "compact").Result;

    public byte[] DeriveLeafSigningKey(string leafId)
        => _remote.DeriveKey("leaf", leafId).Result;

    public byte[] DeriveStaticDepositKey(int index)
        => _remote.DeriveKey("static-deposit", index.ToString()).Result;

    public byte[] GeneratePreimage(string transferId)
        => _remote.HmacSha256("htlc-preimage", transferId).Result;

    public byte[] FrostSign(byte[] m, byte[] sk, byte[] gk, byte[] cs)
        => _remote.FrostSign(m, sk, gk, cs).Result;

    public (byte[] hiding, byte[] binding) GenerateFrostCommitments(byte[] sk)
        => _remote.FrostCommit(sk).Result;
}
```

This is intentionally synchronous (`.Result`) because `ISparkSigner` is
sync. If your remote signer is async-only, gate calls through a bounded
work queue rather than wrapping `Task.Result` everywhere — see the
[Polly bulkhead](https://www.pollydocs.org/strategies/rate-limiter.html)
strategy or your own dispatcher.

## Threading and reentrancy

Every method on `ISparkSigner` is called on whichever thread the wallet
operation runs on. The default `SparkSigner` is thread-safe by virtue of
NBitcoin's immutability. **Your implementation must also be
thread-safe** — `SparkWallet.SendAsync` can run concurrently with
`SparkWallet.PayLightningInvoiceAsync` against the same signer.

## Memory hygiene

The default signer calls `CryptographicOperations.ZeroMemory(...)` on
the local HTLC preimage key buffer after `GeneratePreimage`. The
underlying NBitcoin `ExtKey` retains key material for the lifetime of
the `SparkSigner` instance — that's an intentional trade-off for FROST
signing performance.

A custom signer that never materializes private keys in managed memory
(HSM/KMS) doesn't need this — the keys never leave the secure
boundary. See [`trust-model.md`](trust-model.md) for the documented
process-trust assumption.

## Testing custom signers

Three integration points to verify:

1. **Identity round-trip**: `SignWithIdentityKey(SHA256(challenge))` →
   verify with NBitcoin against `IdentityPublicKey`.
2. **Per-leaf determinism**: `DeriveLeafSigningKey("leaf-1")` returns
   the same bytes on repeated calls; different leaf ids return
   different bytes.
3. **FROST signing acceptance**: run `SparkWallet.SendAsync` against a
   regtest SO cluster; if the SO accepts the transfer, the signer
   produced valid FROST shares.

NSpark's own `SparkFrostBridgeTests` (under
`tests/NSpark.IntegrationTests/`) exercises the FROST primitives
directly — copy that pattern for a custom signer's regression suite.

## See also

- [`architecture.md`](architecture.md) — where `ISparkSigner` sits in
  the layered design.
- [`trust-model.md`](trust-model.md) — what NSpark assumes about the
  signer's environment.
- [`native-build.md`](native-build.md) — rebuilding the `spark_frost`
  native library if you need to swap FROST backends.
