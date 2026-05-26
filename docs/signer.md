# Custom signers (HSM, KMS, hardware wallets, remote services)

NSpark separates **what to sign** from **how to sign**. Every wallet
operation goes through an `ISparkSigner`, and the default
`SparkSigner` is a BIP-39 mnemonic-backed implementation built on
NBitcoin + the native `spark_frost` Rust library. If your application's
key custody is outside the process (HSM, AWS KMS, Azure Key Vault,
hardware wallet, remote signer service), implement `ISparkSigner`
directly and never let a mnemonic near NSpark.

The contract is shaped so that **no plaintext key material, no VSS
shares, no intermediate signing keys, and no tweak-signature payloads
ever cross the wallet's address space**. Everything sensitive happens
inside the signer; the wallet only handles encrypted blobs and
signatures.

## The contract

```csharp
namespace NSpark.Signer;

public interface ISparkSigner
{
    // ── Identity ────────────────────────────────────────────────────────────
    Task<byte[]> GetIdentityPublicKeyAsync(CancellationToken ct = default);
    Task<byte[]> SignWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default);          // DER
    Task<byte[]> SignCompactWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default);   // 64-byte r||s
    Task<byte[]> DecryptEciesWithIdentityKeyAsync(byte[] ciphertext, CancellationToken ct = default);

    // ── Deposit ─────────────────────────────────────────────────────────────
    Task<byte[]> GetDepositPublicKeyAsync(CancellationToken ct = default);

    // ── Per-leaf signing ────────────────────────────────────────────────────
    Task<byte[]> GetLeafPublicKeyAsync(string leafId, CancellationToken ct = default);

    Task<LeafFrostSignature> SignLeafFrostAsync(
        string leafId,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default);

    Task<LeafFrostNonceCommitment> GenerateLeafFrostNonceAsync(
        string leafId,
        CancellationToken ct = default);

    Task<byte[]> SignLeafFrostWithNonceAsync(
        string leafId,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default);

    // ── Per-leaf tweak batches (transfer / claim) ───────────────────────────
    Task<EncryptedSendTweakBatch> BuildEncryptedSendTweaksAsync(
        IReadOnlyList<SendTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        string transferId,
        uint threshold,
        CancellationToken ct = default);

    Task<EncryptedClaimTweakBatch> BuildEncryptedClaimTweaksAsync(
        IReadOnlyList<ClaimTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        CancellationToken ct = default);

    // ── Static deposit ──────────────────────────────────────────────────────
    Task<byte[]> GetStaticDepositPublicKeyAsync(int index = 0, CancellationToken ct = default);
    Task<byte[]> ExportStaticDepositPrivateKeyAsync(int index = 0, CancellationToken ct = default);

    Task<LeafFrostNonceCommitment> GenerateStaticDepositFrostNonceAsync(
        int index, CancellationToken ct = default);

    Task<byte[]> SignStaticDepositFrostWithNonceAsync(
        int index,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        CancellationToken ct = default);

    // ── Lightning receive preimage ──────────────────────────────────────────
    Task<EncryptedPreimageShareBundle> BuildEncryptedPreimageSharesAsync(
        string transferId,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        CancellationToken ct = default);

    // ── Swap adaptor key ────────────────────────────────────────────────────
    Task<AdaptorKeyHandle> GenerateAdaptorKeyAsync(CancellationToken ct = default);
}
```

Every member is async, returns raw bytes or DTO records, and takes a
`CancellationToken` — no NBitcoin or BouncyCastle types leak through.

## When to implement a custom signer

| Scenario                                                 | Recommendation                       |
| -------------------------------------------------------- | ------------------------------------ |
| Single-user desktop wallet, mnemonic loaded from disk    | Default `SparkSigner.FromMnemonic`   |
| Server-side wallet, mnemonic in secret manager           | Default, loaded once at startup       |
| Multi-tenant service, one wallet per user                | Default, one signer per request      |
| Hardware wallet (Ledger, BitBox, etc.)                   | **Custom `ISparkSigner`**            |
| HSM / KMS-backed enterprise wallet                       | **Custom `ISparkSigner`**            |
| Remote signing microservice                              | **Custom `ISparkSigner`**            |
| You never want any private key in process memory         | **Custom `ISparkSigner`**            |

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
SparkWallet wallet = await spark.CreateWalletAsync(signer);  // bypasses mnemonic
```

`CreateWalletAsync(ISparkSigner)` is the entry-point that accepts any
implementation. `SparkConnection` itself is unchanged — the same
singleton handles HSM-backed wallets and mnemonic-backed wallets side
by side. The construction call performs **one** round-trip to the signer
to fetch and cache the identity + deposit public keys so subsequent
accessors (`wallet.IdentityPublicKey`, `wallet.GetSparkAddress()`,
`wallet.DepositPublicKey`) stay synchronous.

## Per-member implementation guide

### Identity key — `GetIdentityPublicKeyAsync`, `SignWithIdentityKeyAsync`, `SignCompactWithIdentityKeyAsync`, `DecryptEciesWithIdentityKeyAsync`

The wallet's BIP-44-style identity key.

- `GetIdentityPublicKeyAsync` — 33-byte compressed secp256k1 point. Used
  for SO/SSP authentication, transfer construction, and as the Spark
  address payload. Called once at wallet construction and cached.
- `SignWithIdentityKeyAsync` — ECDSA over secp256k1, returns DER. Input
  is **already** the 32-byte hash. Used for SO/SSP challenge-response
  authentication, transfer package signatures, claim package signatures,
  static-deposit claim payload signatures, token mint/transfer per-input
  + per-operator signatures.
- `SignCompactWithIdentityKeyAsync` — same key, same input, but returns
  a 64-byte compact `r || s` signature. Used internally by the default
  signer when building per-leaf tweak signatures inside
  `BuildEncryptedSendTweaksAsync`. Most external custom signers won't
  see this called directly because the tweak-signature step lives inside
  the encrypted-batch flow.
- `DecryptEciesWithIdentityKeyAsync` — ECIES decryption of a ciphertext
  addressed to the identity key. The default signer's
  `BuildEncryptedClaimTweaksAsync` calls this internally to recover the
  sender's intermediate signing key from `secret_cipher` blobs; a
  custom signer that overrides `BuildEncryptedClaimTweaksAsync` doesn't
  need to expose this externally.

### Deposit key — `GetDepositPublicKeyAsync`

Used as the user-side spending key inside the FROST-shared deposit
tree. The corresponding private key never needs to sign anything at the
client; it just contributes to the deposit address derivation.

### Per-leaf signing

`GetLeafPublicKeyAsync(leafId)` returns the 33-byte compressed public
key for the per-leaf signing key. The default derives this via BIP-32
hardened derivation:
`m/8797555' / account' / 1' / (SHA256(leafId)[0:4] % 2^31 + 2^31)`.
A custom signer can pick any deterministic scheme as long as the same
leaf id always maps to the same public key.

`SignLeafFrostAsync(leafId, message, verifyingKey, soCommitments, adaptorPublicKey?)`
runs a complete one-shot FROST signing round: generate the user's
hiding+binding nonce, sign the message against the supplied SO
commitments, return `(publicKey, commitment, userSignature)`. Used by
transfer / claim / Lightning send / swap / deposit-tree-creation
flows.

`GenerateLeafFrostNonceAsync` / `SignLeafFrostWithNonceAsync` form a
**two-phase** signing protocol. Phase 1 returns the user's commitment
plus an opaque `nonceHandle`. The wallet publishes the commitment to
the SOs, the SOs return the final transaction (e.g., the
cooperative-exit tx with the connector input added), the wallet
computes the resulting sighash, and phase 2 signs that sighash using
the same nonce. The handle is opaque — for the default in-process
signer it carries the secret nonce + the public commitment so phase 2
doesn't need any extra state; for a remote signer it can be an
encrypted blob or a server-side key.

### Per-leaf tweak batches — `BuildEncryptedSendTweaksAsync`, `BuildEncryptedClaimTweaksAsync`

These are the heart of the encrypted-batch design. The wallet
never sees raw shares, intermediate signing keys, or tweak signature
payloads — the signer does the whole pipeline and returns
ECIES-encrypted `SendLeafKeyTweaks` / `ClaimLeafKeyTweaks` proto blobs
ready to go on the wire.

`BuildEncryptedSendTweaksAsync(leaves, soTargets, transferId, threshold)`
— for each leaf:

1. Derive the leaf's current signing key
2. Generate a fresh intermediate signing key
3. Compute `tweak = (oldKey - newKey) mod n`
4. VSS-split the tweak into `soTargets.Count` shares
5. ECIES-encrypt the intermediate key to the leaf's `ReceiverPublicKey`
   → `secret_cipher`
6. Sign `SHA256(leafId || transferId || secret_cipher)` with the
   compact ECDSA identity-key signature
7. Build one `SendLeafKeyTweak` proto per (leaf, SO) pair with the
   correct share, secret_cipher, signature, and per-SO pubkey-shares
   map
8. Fold all leaves into one `SendLeafKeyTweaks` proto per SO
9. ECIES-encrypt each per-SO proto to that SO's identity public key
10. Return `{ soId → encryptedBlob }`

`BuildEncryptedClaimTweaksAsync` is the mirror operation for the
receive side: ECIES-decrypts the sender's `secret_cipher`, derives the
receiver's new per-leaf signing key, computes the tweak, VSS-splits,
and ECIES-encrypts per-SO `ClaimLeafKeyTweaks` blobs. The new per-leaf
public key for each leaf is returned alongside (the wallet needs it as
the receiving pubkey when constructing refund txs).

### Static deposit

`GetStaticDepositPublicKeyAsync(index)` returns the public key. Used to
generate the on-chain static deposit address.

`GenerateStaticDepositFrostNonceAsync` / `SignStaticDepositFrostWithNonceAsync`
are the same two-phase FROST signing protocol described above, but for
the static-deposit signing key. Used by the static-deposit refund flow.

`ExportStaticDepositPrivateKeyAsync(index)` — this is the **one**
intentional private-key escape hatch in the signer surface. The Spark
static-deposit protocol requires revealing the raw private key to the
SSP so the SSP can sweep the on-chain UTXO into a leaf. HSM-backed
signers that refuse to export raw key material should throw
`NotSupportedException`; static-deposit claims (`ClaimStaticDepositAsync`)
will then be unavailable, but every other operation works normally.

### Lightning preimage — `BuildEncryptedPreimageSharesAsync`

The Lightning receive flow doesn't generate a random preimage — it
asks the signer to deterministically derive one from `transferId`
(default impl: `HMAC-SHA256(htlcPreimageKey, UTF8(transferId))`),
hash it to produce the BOLT11 payment hash, VSS-split it into
per-SO shares, wrap each share in a `SecretShare` proto, and ECIES-
encrypt that proto to the matching SO's identity public key. The
preimage and the raw share scalars never leave the signer; only the
public payment hash and the per-SO encrypted blobs do.

Determinism is load-bearing: if the wallet crashes between issuing the
invoice and storing the shares with the SOs, calling this method again
with the same `transferId` MUST produce the same payment hash so the
in-flight payment can still be claimed.

### Swap adaptor key — `GenerateAdaptorKeyAsync`

Returns `(PublicKey, Handle)` where the handle is opaque to the wallet.
The default impl puts the raw 32-byte private scalar inside the handle
(in-process trust boundary); a remote signer can put any opaque token.
Currently the wallet uses only the public key (the private half is
reserved for the future swap-completion path).

## Reference: minimal signer that proxies a remote service

```csharp
using NSpark.Signer;
using NSpark.Services;
// Hypothetical async client to your signing service:
public interface IRemoteSigner
{
    Task<byte[]> GetPublicKeyAsync(string id, CancellationToken ct);
    Task<byte[]> SignDerAsync(string keyId, byte[] hash, CancellationToken ct);
    Task<byte[]> SignCompactAsync(string keyId, byte[] hash, CancellationToken ct);
    Task<byte[]> DecryptEciesAsync(string keyId, byte[] ciphertext, CancellationToken ct);
    Task<byte[]> GetLeafPublicKeyAsync(string leafId, CancellationToken ct);

    Task<(byte[] PublicKey, byte[] HidingCommit, byte[] BindingCommit, byte[] UserSig)>
        SignLeafFrostAsync(
            string leafId, byte[] message, byte[] verifyingKey,
            IReadOnlyDictionary<string, (byte[] H, byte[] B)> soCommitments,
            byte[]? adaptorPubKey, CancellationToken ct);

    Task<IReadOnlyDictionary<string, byte[]>> BuildEncryptedSendTweaksAsync(
        IReadOnlyList<(string LeafId, byte[] ReceiverPubKey)> leaves,
        IReadOnlyList<(string SoId, uint ShareIdx, byte[] SoPubKey)> soTargets,
        string transferId, uint threshold, CancellationToken ct);
    // ... etc ...
}

public sealed class RemoteSparkSigner : ISparkSigner
{
    private readonly IRemoteSigner _remote;
    public RemoteSparkSigner(IRemoteSigner remote) => _remote = remote;

    public Task<byte[]> GetIdentityPublicKeyAsync(CancellationToken ct = default)
        => _remote.GetPublicKeyAsync("identity", ct);

    public Task<byte[]> GetDepositPublicKeyAsync(CancellationToken ct = default)
        => _remote.GetPublicKeyAsync("deposit", ct);

    public Task<byte[]> GetLeafPublicKeyAsync(string leafId, CancellationToken ct = default)
        => _remote.GetLeafPublicKeyAsync(leafId, ct);

    public Task<byte[]> GetStaticDepositPublicKeyAsync(int index = 0, CancellationToken ct = default)
        => _remote.GetPublicKeyAsync($"static-deposit:{index}", ct);

    public Task<byte[]> SignWithIdentityKeyAsync(byte[] hash, CancellationToken ct = default)
        => _remote.SignDerAsync("identity", hash, ct);

    public Task<byte[]> SignCompactWithIdentityKeyAsync(byte[] hash, CancellationToken ct = default)
        => _remote.SignCompactAsync("identity", hash, ct);

    public Task<byte[]> DecryptEciesWithIdentityKeyAsync(byte[] ciphertext, CancellationToken ct = default)
        => _remote.DecryptEciesAsync("identity", ciphertext, ct);

    public async Task<LeafFrostSignature> SignLeafFrostAsync(
        string leafId, byte[] message, byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null, CancellationToken ct = default)
    {
        var soMap = soCommitments.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.Hiding, kv.Value.Binding));
        var r = await _remote.SignLeafFrostAsync(
            leafId, message, verifyingKey, soMap, adaptorPublicKey, ct);
        return new LeafFrostSignature(
            r.PublicKey,
            new SigningCommitment(r.HidingCommit, r.BindingCommit),
            r.UserSig);
    }

    public async Task<EncryptedSendTweakBatch> BuildEncryptedSendTweaksAsync(
        IReadOnlyList<SendTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        string transferId, uint threshold, CancellationToken ct = default)
    {
        var leafTuples = leaves.Select(l => (l.LeafId, l.ReceiverPublicKey)).ToList();
        var soTuples = soTargets.Select(s => (s.SoId, s.ShareIndex, s.IdentityPublicKey)).ToList();
        var blobs = await _remote.BuildEncryptedSendTweaksAsync(
            leafTuples, soTuples, transferId, threshold, ct);
        return new EncryptedSendTweakBatch(blobs);
    }

    // ... GenerateLeafFrostNonceAsync, SignLeafFrostWithNonceAsync,
    //     BuildEncryptedClaimTweaksAsync, BuildEncryptedPreimageSharesAsync,
    //     GenerateStaticDepositFrostNonceAsync, SignStaticDepositFrostWithNonceAsync,
    //     ExportStaticDepositPrivateKeyAsync, GenerateAdaptorKeyAsync ...

    public Task<AdaptorKeyHandle> GenerateAdaptorKeyAsync(CancellationToken ct = default)
        => throw new NotImplementedException("see sketch above");

    public Task<byte[]> ExportStaticDepositPrivateKeyAsync(int index = 0, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Static deposit private keys cannot be exported from the remote signer. " +
            "ClaimStaticDepositAsync is unavailable; use the cooperative-exit flow instead.");

    // ... remaining methods left as exercise; pattern is the same ...
}
```

Because the interface is fully async, the call sites use `await`
naturally — no `.Result` deadlocks, no thread-pool starvation, no
need for Polly bulkhead workarounds.

## Composing existing primitives

If a custom signer doesn't want to implement everything from scratch,
two public helpers are available outside the signer itself:

- **`NSpark.Services.SparkTxBuilder`** — public Spark-protocol Bitcoin
  transaction construction (refund tx trio, HTLC tx, node tx pair,
  multi-input sighash). Pure public-key operations; safe to call from
  any custom signer that needs to construct the same protocol-level
  transactions.
- **`NSpark.Signer.FrostAggregator`** — public FROST signature
  aggregation (combine self + SO partial signatures into the final
  signature). Pure public-key operation. Useful inside custom
  `SignLeafFrostWithNonceAsync` implementations when you need to
  aggregate the result before returning.

Both helpers are safe to call outside any signer trust boundary — they
take only public material.

## Threading and reentrancy

Every method on `ISparkSigner` is called on whichever thread the wallet
operation runs on, often in parallel during leaf-heavy flows. The
default `SparkSigner` is thread-safe by virtue of NBitcoin's
immutability + the per-call key derivation. **Your implementation must
also be thread-safe** — `SparkWallet.SendAsync` can run concurrently
with `SparkWallet.PayLightningInvoiceAsync` against the same signer,
and within a single flow many leaves' nonces / FROST signatures are
generated in parallel.

## Memory hygiene

The default signer calls `CryptographicOperations.ZeroMemory(...)` on
every locally-materialised private scalar after use. The underlying
NBitcoin `ExtKey` retains key material for the lifetime of the
`SparkSigner` instance — that's an intentional trade-off for FROST
signing performance.

A custom signer that never materialises private keys in managed memory
(HSM/KMS) doesn't need this — the keys never leave the secure
boundary. See [`trust-model.md`](trust-model.md) for the documented
process-trust assumption.

## Testing custom signers

Three integration points to verify:

1. **Identity round-trip**: `SignWithIdentityKeyAsync(SHA256(challenge))`
   → verify the signature with any secp256k1 library against
   `GetIdentityPublicKeyAsync()`.
2. **Per-leaf determinism**: `GetLeafPublicKeyAsync("leaf-1")` returns
   the same bytes on repeated calls; different leaf ids return
   different bytes.
3. **End-to-end acceptance**: run `SparkWallet.SendAsync` against a
   regtest SO cluster; if the SOs accept the transfer, your signer
   produced valid FROST partials, valid tweak shares, valid per-SO
   ECIES packages, and valid identity-key signatures over the
   package hash.

NSpark's own `SparkFrostBridgeTests` (under
`tests/NSpark.IntegrationTests/`) exercises the FROST primitives
directly — copy that pattern for a custom signer's regression suite.
The `[Explicit]` tests in `SparkWalletIntegrationTests.cs` are a
ready-made acceptance suite when pointed at a funded regtest cluster.

## See also

- [`architecture.md`](architecture.md) — where `ISparkSigner` sits in
  the layered design.
- [`trust-model.md`](trust-model.md) — what NSpark assumes about the
  signer's environment.
- [`native-build.md`](native-build.md) — rebuilding the `spark_frost`
  native library if you need to swap FROST backends.
