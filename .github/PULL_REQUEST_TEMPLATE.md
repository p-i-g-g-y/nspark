<!-- Thanks for opening a pull request! Please complete the checklist below. -->

## Summary

<!-- One paragraph: what does this PR change and why? -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Documentation / samples
- [ ] Refactor (no behavior change)
- [ ] Breaking change (public API)
- [ ] CI / build infrastructure

## Public API impact

- [ ] No public API changes.
- [ ] `src/NSpark/PublicAPI.Unshipped.txt` updated.
- [ ] `CHANGELOG.md` updated under **Unreleased**.

## Verification

- [ ] `dotnet build --configuration Release` is clean.
- [ ] `dotnet test --filter "FullyQualifiedName!~SparkWalletIntegration"` passes.
- [ ] Integration tests run against regtest *(if behavior change touches Lightning / transfer flows)*.
- [ ] Manual verification: <!-- describe -->

## Security checklist

- [ ] No new logging of preimages, mnemonics, private keys, FROST shares, ECIES ciphertexts, or bearer tokens.
- [ ] No new public API expands the trust boundary documented in [`docs/trust-model.md`](../docs/trust-model.md).
- [ ] If this PR adds a new dependency, it is enumerated in `THIRD_PARTY_NOTICES.md`.

## Related

<!-- Issue numbers, discussions, RFCs. -->

Closes #
