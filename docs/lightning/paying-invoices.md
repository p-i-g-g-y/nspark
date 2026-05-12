# Sending Lightning payments

NSpark wallets pay BOLT11 invoices and Lightning addresses (LNURL-pay /
LUD-16). Both flows are extension methods on `SparkWallet`.

## Pay a BOLT11 invoice

```csharp
using NSpark;
using NSpark.Services;

string paymentRequest = "lnbc1u1p4qx..."; // user-supplied
long maxFeeSats = 50;                     // your routing-fee cap

var paymentId = await wallet.PayLightningInvoiceAsync(
    paymentRequest,
    maxFeeSats: maxFeeSats);

Console.WriteLine($"Payment id: {paymentId}");
```

`PayLightningInvoiceAsync` returns the SSP-side payment id (e.g.
`SparkLightningSendRequest:019e1c4...`), which is the canonical identifier
you persist for reconciliation. The call resolves only once the payment
either settles or the SSP gives up.

> **Set `maxFeeSats` in production.** Lightning routing fees are not
> bounded by the protocol — only by what your wallet is willing to pay.
> Without `maxFeeSats`, NSpark allows the SSP-quoted fee unconditionally.

## Pay a Lightning address (LNURL-pay / LUD-16)

```csharp
var paymentId = await wallet.PayLightningAddressAsync(
    "user@example.com",
    amountSats: 100,
    maxFeeSats: 10);
```

Under the hood `PayLightningAddressAsync`:

1. Fetches `https://example.com/.well-known/lnurlp/user` via HTTPS.
2. Reads the LNURL-pay metadata (callback URL, `minSendable`, `maxSendable`).
3. Validates `amountSats * 1000` is within the sendable msat range.
4. Calls the callback to get a freshly issued BOLT11.
5. Delegates to `PayLightningInvoiceAsync`.

Failures surface as `SparkLightningException` (LNURL response invalid,
amount out of range, callback errored) or `PaymentFailedException` (the
underlying BOLT11 payment failed).

## What "payment" means in NSpark

A Lightning send is conceptually three steps:

1. **Decode + select**. NSpark parses the BOLT11 payment hash + amount,
   then selects leaves to fund it. If no exact-value leaf exists, the
   wallet swaps leaves with the SSP first.
2. **HTLC construction**. The wallet builds three HTLC refund
   transactions per input (CPFP, direct, directFromCpfp) and FROST-signs
   each branch with the Signing Operators.
3. **Preimage swap**. The wallet hands the package to the SSP
   (`initiate_preimage_swap_v3`). The SSP pays the BOLT11, retrieves the
   preimage, and atomically claims the HTLC leaves — at which point the
   wallet's leaves move out of `AVAILABLE` and the payment is final.

Because the preimage swap is atomic, **either the payment succeeds and
the leaves are spent, or it fails and the leaves stay yours**. There's
no partial state to clean up on the client.

## Estimating routing fees before paying

```csharp
var feeSats = await wallet.GetLightningSendFeeEstimateAsync(paymentRequest);
Console.WriteLine($"Fee estimate: {feeSats} sats");
```

The estimate comes from the SSP and is typically within a few sats of
the realized fee. Use it to inform the user before committing, or as a
sanity check on `maxFeeSats`.

## Failure modes

| Exception                       | Meaning                                                    | Retryable?                          |
| ------------------------------- | ---------------------------------------------------------- | ----------------------------------- |
| `InvalidBolt11Exception`        | The payment-request string couldn't be decoded.            | ❌ caller bug, fix the input         |
| `InsufficientFundsException`    | Available sats < amount + max fee.                         | ❌ until the wallet has more sats    |
| `PaymentFailedException`        | SSP routing failed (no path / unreachable destination).    | Sometimes — wait + try again        |
| `InvoiceExpiredException`       | The BOLT11 has expired between decode and pay.             | ❌ get a new invoice                 |
| `SparkConnectionException`      | gRPC / HTTP transport problem.                             | Check `IsRetryable`                 |
| `SparkAuthenticationException`  | Wallet identity rejected by SO or SSP.                     | ❌ until the wallet re-authenticates |

Everything else falls back to `SparkLightningException`. See
[`../error-handling.md`](../error-handling.md) for the full hierarchy.

## Idempotency

Lightning sends are **not idempotent at the protocol level** — paying the
same BOLT11 twice could pay twice if the first attempt was racing. Two
practical guards:

- BOLT11 payment hashes are unique per invoice. The SSP rejects
  duplicate active sends to the same hash.
- Persist the returned `paymentId` before showing "payment sent" in your
  UI; on retry/replay, check the SSP status via that id before
  re-issuing.

```csharp
try
{
    var paymentId = await wallet.PayLightningInvoiceAsync(req, maxFeeSats: 50, ct);
    await ledger.RecordPayment(paymentId, req, /* status */ "submitted");
}
catch (Exception ex)
{
    // Was the payment actually submitted? Check by payment hash before retrying.
    await ledger.RecordPaymentError(req, ex);
    throw;
}
```

## Cancellation

Pass a `CancellationToken` to abort the local wait. Cancellation **does
not abort the in-flight Lightning payment** — once NSpark hands the
package to the SSP, the SSP runs the swap to completion. Cancellation
just stops your code from awaiting the result. Use the SSP's
status-query endpoint (the returned payment id) to learn the outcome.

## Sending to another NSpark wallet

You don't have to use Lightning for in-Spark transfers — `SendAsync`
moves leaves directly between wallets with no routing fee:

```csharp
var receiverPubKey = Convert.FromHexString(otherWallet.IdentityPublicKeyHex);
var transfer = await wallet.SendAsync(receiverPubKey, amountSats: 1_000);
```

The receiver still has to `ClaimPendingTransfersAsync()`. Use Lightning
when the destination is outside the Spark network; use Spark transfers
inside it.
