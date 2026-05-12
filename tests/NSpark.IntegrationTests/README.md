# NSpark integration tests

These tests talk to live Spark Signing Operators (or a local regtest cluster)
and need real BIP-39 mnemonics for funded wallets. They are **not** run in
CI by default — `.github/workflows/ci.yml` runs only the unit-test project.
When a required `NSPARK_TEST_MNEMONIC_*` env var is missing, every test that
needs it is reported as **inconclusive** (skipped), not failed, so the suite
stays green for contributors who haven't wired up funded wallets.

## Setup

1. Copy [`.env.local.example`](../../.env.local.example) at the repository
   root to `.env.local`.
2. Fill in three BIP-39 mnemonics:
   - `NSPARK_TEST_MNEMONIC_A` — primary wallet (issues + holds tokens)
   - `NSPARK_TEST_MNEMONIC_B` — counterparty (receives transfers)
   - `NSPARK_TEST_MNEMONIC_C` — third party (multi-wallet flows)
3. Fund them:
   - **Sats**: a few thousand each. Wallet A needs more to cover the token
     lifecycle (mint + transfer + burn produces several intra-Spark txns).
   - **Tokens (optional)**: only the `TokenLifecycleTests` need tokens, and
     they will issue a token on first run if wallet A doesn't already have
     one with itself as the issuer. Reuses the existing token on subsequent
     runs.
4. Run the full test suite:
   ```bash
   dotnet test tests/NSpark.IntegrationTests/NSpark.IntegrationTests.csproj
   ```

   Or limit to a specific area:
   ```bash
   # Only token tests:
   dotnet test --filter TestCategory=Tokens

   # Only balance / read-side:
   dotnet test --filter "FullyQualifiedName~BalanceTests|FullyQualifiedName~TokenReadTests"
   ```

You can also export the variables directly in your shell or your CI's
secrets store — process environment values always win over `.env.local`.

## Test fixtures

| Fixture | Wallets | What it covers |
|---|---|---|
| `WalletTests` | A | Mnemonic-derived identity keys, account separation |
| `SparkAddressTests` | A | Spark address bech32m round-trip |
| `BalanceTests` | A | `GetBalanceAsync` / `GetLeavesAsync` against live nodes |
| `DepositTests` | A, B | On-chain deposit address generation + claim |
| `LightningTests` | A, B | BOLT11 invoice creation + payment |
| `DelegatedInvoiceTests` | A, B | Description-hash invoices for NIP-57 zaps |
| `TransferTests` | A, B, C | Spark-to-Spark transfers + pending claim flow |
| `SwapTests` | A, B, C | Leaf swaps via the SSP |
| `WithdrawalTests` | A | On-chain withdrawal |
| `StaticDepositTests` | A | Static deposit address claim |
| `OnChainDepositTests`, `ThirdPartyInvoiceTests`, `FundingTests` | A | Cross-wallet on-chain + funding flows |
| `FullFlowTests` | A, B | End-to-end: deposit → transfer → external Lightning |
| `TokenReadTests` | A | `GetTokenOutputsAsync`, `GetTokenBalancesAsync`, balance join |
| `TokenLifecycleTests` | A, B | Create / mint / transfer A→B / transfer B→A / burn |

## Behavior when secrets are missing

`TestSecrets.Require(...)` calls `Assert.Inconclusive(...)` when an env var
is unset. NUnit reports the test as **inconclusive** (skipped), not failed.
This keeps CI green for the non-integration subset and lets contributors
without funded wallets work on the unit tests and SDK code freely.

## Security notes

- `.env.local` is git-ignored (`.gitignore` rules `.env*` and a positive
  rule for `.env.local.example`). Verify with `git status` before
  committing.
- Never reuse mainnet mnemonics across environments. Cycle them after any
  suspected exposure.
- The mnemonics used in earlier versions of this repository — `"ice around
  predict ..."`, `"profit object awkward ..."`, `"fence okay spatial ..."`,
  `"crawl cattle forward ..."` — were committed to git history. Treat them
  as **public** and do not fund them.
