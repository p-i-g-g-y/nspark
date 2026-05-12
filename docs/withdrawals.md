# On-chain withdrawals

Withdraw Spark sats back to a Bitcoin L1 address via a **cooperative
exit** brokered by the SSP. The wallet:

1. Selects leaves to cover the amount (swap if no exact match).
2. Asks the SSP to quote a cooperative-exit transaction.
3. Co-signs the SSP's connector transaction with FROST against the
   Signing Operators.
4. The SSP broadcasts the spending transaction on-chain.

The returned identifier is an on-chain transaction id.

## Quote the fee first

```csharp
using NSpark;
using NSpark.Services;

var quote = await wallet.GetFeeQuoteAsync(amountSats: 10_000);
Console.WriteLine($"Network fee: {quote.FeeSats} sats @ {quote.FeeRateSatsPerVbyte} sat/vB");
```

The quote is informational — the actual fee is computed by the SSP at
withdraw time. Use it to show the user the expected cost before
confirming.

## Withdraw

```csharp
var txid = await wallet.WithdrawAsync(
    onChainAddress: "bc1q...",     // user-supplied destination
    amountSats: 10_000,
    ct: cancellationToken);

Console.WriteLine($"Withdrawal tx: {txid}");
```

After this returns, the SSP has built and broadcast the spending
transaction; the leaves that funded it are out of `AVAILABLE` state
and will not show up on subsequent `GetBalanceAsync` calls.

> **Destructive.** Withdrawal spends balance. There's no second
> claim — the destination receives sats on L1 directly.

## What happens behind the scenes

The withdrawal flow goes through the SSP's `RequestCoopExit` GraphQL
mutation:

1. The wallet picks leaves that sum to the amount, swapping with the
   SSP first if no exact-value combination exists.
2. The SSP returns a partially-constructed Bitcoin transaction (the
   "connector transaction") that pays the destination + the SSP's fee.
3. The wallet builds per-leaf refund signing jobs against the connector
   transaction with a 7-day expiry, signs with FROST against the SOs.
4. The wallet submits the signed package to the SSP via
   `complete_coop_exit`.
5. The SSP broadcasts the final spending transaction on-chain and
   returns the txid.

The 7-day expiry is intentional: if for some reason the SSP fails to
broadcast or the broadcast fails, the wallet can use the pre-signed
refund transactions to spend the leaves on-chain unilaterally after the
expiry. NSpark does not expose the unilateral-exit path directly in
v0.1.x — file an issue if you need it for your use case.

## Selecting which leaves to spend

`WithdrawAsync` delegates to `SelectLeavesWithSwapAsync(amount)`, which:

1. Looks for an exact-amount leaf match.
2. Falls back to a multi-leaf combination that sums to the amount.
3. If neither exists, requests a leaf swap from the SSP (the SSP
   exchanges your leaves for a freshly-sized leaf via two intra-Spark
   transfers) and retries.

You can pre-swap explicitly if you want to control fees:

```csharp
var leaves = await wallet.SelectLeavesWithSwapAsync(10_000);
// leaves now sum to exactly 10_000 (or throws)
var txid = await wallet.WithdrawAsync("bc1q...", 10_000);
```

## Errors

| Exception                       | Cause                                                       |
| ------------------------------- | ----------------------------------------------------------- |
| `InsufficientFundsException`    | Available sats < amount.                                    |
| `SparkWithdrawalException`      | SSP refused the quote (e.g. amount below dust threshold).    |
| `SparkConnectionException`      | gRPC / HTTP transport problem. Often retryable.             |
| `SparkAuthenticationException`  | Wallet identity rejected by SO or SSP.                      |
| `SparkSignerException`          | FROST signing failed locally.                               |

See [`error-handling.md`](error-handling.md) for the full hierarchy.

Most withdrawal failures happen at the SSP quote step (insufficient
liquidity, amount too small, destination address malformed). NSpark
surfaces those as `SparkWithdrawalException` with the SSP's
human-readable reason.

## Idempotency

The `transferId` NSpark generates for each `WithdrawAsync` call is a
fresh UUID — replays produce a new on-chain transaction. If your
application retries on transient errors, you **must** persist the
returned txid and check it against the SSP's status endpoint before
re-issuing the withdrawal.

The simplest pattern: wrap your withdrawal in an outer try/catch that
catches `SparkConnectionException`, polls the SSP for the most recent
exit request status before retrying.

## See also

- [`deposits.md`](deposits.md) — the inbound side of L1 ↔ Spark
  movement.
- [`error-handling.md`](error-handling.md) — what each exception means.
- [`trust-model.md`](trust-model.md) — what the SSP can and cannot do
  during a withdrawal.
