# NSpark samples

End-to-end examples that compile against the in-repo `src/NSpark/` project.
Run each from the repository root.

## QuickStart

A console app that creates a wallet from a BIP-39 mnemonic, mints a
1,000-sat Lightning invoice, and prints the wallet balance. Useful as the
first thing you copy when starting a new project.

```bash
# Use the default test mnemonic (will fail against mainnet — that's expected).
dotnet run --project samples/QuickStart

# Use your own wallet against mainnet.
NSPARK_MNEMONIC="word word word ... word" dotnet run --project samples/QuickStart

# Use a local regtest setup.
NSPARK_NETWORK=regtest dotnet run --project samples/QuickStart
```

## Coming soon

- `samples/AspNetCore` — minimal-API receiver with OpenTelemetry wired up.
- `samples/CustomSigner` — implementing `ISparkSigner` against a fake HSM.
- `samples/InvoiceServer` — full Lightning receive flow with DB persistence.

Contributions welcome — see [`CONTRIBUTING.md`](../CONTRIBUTING.md).
