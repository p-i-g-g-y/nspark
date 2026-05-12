# Release process

NSpark uses [MinVer](https://github.com/adamralph/minver) to derive the
package version from git tags. The full release happens via
[`.github/workflows/release.yml`](../.github/workflows/release.yml) on
tag push.

## One-time setup (per repo / per maintainer)

1. **NuGet API key**
   - Sign in to [nuget.org](https://www.nuget.org).
   - Profile → API Keys → **Create**.
     - Key name: `NSpark Release CI`
     - Scope: **Push** (push new + push new versions of existing)
     - Glob pattern: `NSpark*` (or `NSpark` if you want it locked down to
       just the canonical package id).
     - Expiration: 365 days. Add a reminder to rotate.
   - Copy the generated key (it's shown only once).
   - In GitHub: **Settings → Secrets and variables → Actions → New
     repository secret**.
     - Name: `NUGET_API_KEY`
     - Value: the key you just copied.
   - Path in the UI:
     `https://github.com/p-i-g-g-y/nspark/settings/secrets/actions`

2. **Optional: Codecov token** (for coverage uploads — already wired up
   in `ci.yml` but skipped if absent)
   - From <https://app.codecov.io/gh/p-i-g-g-y/nspark> after
     authorizing the repo.
   - Secret name: `CODECOV_TOKEN`.

3. **Optional: code-signing certs** (deferred until OrkLabs acquires
   them; v0.x ships unsigned with SHA-256 + SLSA attestation):
   - Apple Developer ID Application cert → notarize `*.dylib`
   - Authenticode OV cert → sign `*.dll`
   - GPG key for git tag signing
   - The `native-build.yml` workflow has the scaffolding ready for when
     these arrive.

## Cutting a release

```bash
# Ensure main is clean and tests pass locally.
dotnet test tests/NSpark.UnitTests/NSpark.UnitTests.csproj -c Release

# Move every line in PublicAPI.Unshipped.txt to PublicAPI.Shipped.txt
# (for v1.0+ releases only — prereleases can leave entries in Unshipped).
# Commit that move.

# Tag and push.
git tag v0.1.0-alpha.1
git push origin v0.1.0-alpha.1
```

`release.yml` then:

1. Restores + builds in Release with deterministic flags.
2. Runs the unit test suite.
3. `dotnet pack` — MinVer reads the tag and stamps the package version
   (`NSpark.0.1.0-alpha.1.nupkg`).
4. Generates an SBOM with CycloneDX (continue-on-error if the tool
   transiently fails).
5. Generates a SLSA build provenance attestation via
   `actions/attest-build-provenance@v2` — OIDC-based, no certs needed.
6. Pushes to nuget.org with `--skip-duplicate`.
7. Creates a GitHub Release with auto-generated notes from the commit
   diff, attaches the `.nupkg` + `.snupkg` + SBOM + `SHA256SUMS.txt`,
   and marks it as prerelease whenever the tag name contains a hyphen
   (`v0.1.0-alpha.1` → prerelease, `v1.0.0` → stable).

## Release flavors

| Tag | What it means | Package id |
|---|---|---|
| `v0.1.0-alpha.N` | Early preview, breaking changes expected | `NSpark.0.1.0-alpha.N` |
| `v0.1.0-beta.N`  | Feature complete, breaking changes still allowed | `NSpark.0.1.0-beta.N` |
| `v0.1.0-rc.N`    | Release candidate, no behavior changes besides bug fixes | `NSpark.0.1.0-rc.N` |
| `v1.0.0`         | Stable, semver enforced via `PublicAPI.Shipped.txt` | `NSpark.1.0.0` |
| `v1.0.1`         | Patch release | `NSpark.1.0.1` |

## Verifying a release

Consumers can verify a downloaded `.nupkg`:

```bash
# SHA-256 of bundled native binaries.
unzip -p NSpark.<version>.nupkg runtimes/SHA256SUMS.txt
( cd ./runtimes && shasum -a 256 -c SHA256SUMS.txt )

# SLSA build provenance attestation.
gh attestation verify NSpark.<version>.nupkg --owner p-i-g-g-y
```
