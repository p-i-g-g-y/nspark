# Contributing to NSpark

Thanks for considering a contribution. NSpark is built in the open and we
welcome bug reports, feature proposals, documentation improvements, and code
changes from anyone in the Bitcoin / Lightning / .NET communities.

Please read [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) before participating in
discussions or sending patches.

## Before you start

- **Vulnerabilities**: never open a public issue for a security bug. Follow the
  disclosure process in [`SECURITY.md`](SECURITY.md).
- **Larger changes**: open a GitHub Discussion or draft issue first so we can
  align on scope and architecture. Avoid sinking time into a 1,000-line PR
  that re-architects a subsystem only to discover the maintainers were going
  the other direction.
- **Public API**: every public symbol is tracked in
  `src/NSpark/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Any change
  to those files needs a `CHANGELOG.md` entry under **Unreleased** and a
  clear note in the PR description.

## Development environment

```bash
# Requires the .NET 10 SDK (pinned by global.json) plus the net8 + net9 packs.
dotnet --version          # should report 10.0.x
dotnet workload restore   # if any are required
dotnet restore
dotnet build -c Debug
dotnet test  --filter "FullyQualifiedName!~SparkWalletIntegration"
```

Integration tests are gated by an environment variable so they do not run
against the public network by default. See
[`tests/NSpark.IntegrationTests/README.md`](tests/NSpark.IntegrationTests/README.md)
once it's filled in (TODO).

### Native FROST library

The FROST signing primitives live in a separate Rust crate. The pre-built
binaries shipped in `src/NSpark/runtimes/<rid>/native/` are sufficient for
contributing to the C# side. To rebuild them yourself, follow
[`docs/native-build.md`](docs/native-build.md).

## Coding conventions

- Follow `.editorconfig`. Run `dotnet format` before pushing.
- C# 12+ idioms where they read naturally (file-scoped namespaces, primary
  constructors, collection expressions, target-typed `new`).
- `Nullable` is enabled repository-wide — fix nullability warnings rather
  than annotating them away.
- All async methods must take a `CancellationToken` as the last parameter and
  call `ConfigureAwait(false)` on awaits — enforced by `CA2007`.
- New public APIs require:
  - XML doc comments
  - An entry in `PublicAPI.Unshipped.txt`
  - A test (unit when possible, integration when not)
  - A `CHANGELOG.md` entry

## Commit and PR style

- One logical change per commit; many small commits per PR are welcome.
- Subject line in the imperative mood, ≤ 72 characters.
- Reference issues with `Fixes #123` / `Closes #123` in the description.
- PR titles follow the same convention as commit subjects.
- We squash-merge most PRs. Keep the squash subject high-quality.

## Reporting bugs

When opening a bug, please include:

1. NSpark version, .NET TFM, OS, RID.
2. The smallest reproducible code sample you can manage.
3. Expected vs. actual behaviour.
4. Relevant log output. Run with structured logging enabled and include the
   `EventId`s from `NSpark.Diagnostics.LogEvents` near the failure.

## Releases

Maintainers cut releases by tagging `vX.Y.Z` on `main`; the
[`Release` workflow](.github/workflows/release.yml) packs, attests, and
publishes to nuget.org automatically. See
[`docs/release-process.md`](docs/release-process.md) for the full
procedure and required secrets.

Thanks again — see you in the PR queue.
