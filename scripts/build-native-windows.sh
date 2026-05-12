#!/usr/bin/env bash
# Build the spark_frost Windows DLLs from a macOS / Linux host using cargo-xwin.
#
# Usage:
#   ./scripts/build-native-windows.sh [path-to-spark-repo]
#
# When the path argument is omitted, the script clones the buildonspark/spark
# repository into a temporary directory.
#
# Output: copies the freshly built DLLs into
#   src/NSpark/runtimes/win-x64/native/spark_frost.dll
#   src/NSpark/runtimes/win-arm64/native/spark_frost.dll
#
# Prerequisites (one-time setup):
#   brew install llvm                                   # macOS only
#   cargo install cargo-xwin
#   rustup target add x86_64-pc-windows-msvc aarch64-pc-windows-msvc

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPARK_REPO="${1:-}"
KEEP_CLONE=true
TARGETS=(x86_64-pc-windows-msvc aarch64-pc-windows-msvc)

# Map Rust target triple -> NuGet RID. Implemented as a function to stay
# compatible with macOS-default Bash 3.2 (no associative arrays).
target_to_rid() {
    case "$1" in
        x86_64-pc-windows-msvc)  echo "win-x64" ;;
        aarch64-pc-windows-msvc) echo "win-arm64" ;;
        *) echo "unknown" ;;
    esac
}

log() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
err() { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; }

# --- Preflight checks -------------------------------------------------------

command -v cargo >/dev/null 2>&1 || { err "cargo not found — install Rust via rustup."; exit 1; }
command -v cargo-xwin >/dev/null 2>&1 || { err "cargo-xwin not found — run: cargo install cargo-xwin"; exit 1; }

for target in "${TARGETS[@]}"; do
    if ! rustup target list --installed | grep -q "^${target}$"; then
        err "Rust target '${target}' not installed. Run: rustup target add ${target}"
        exit 1
    fi
done

# --- Get the Spark source ---------------------------------------------------

if [[ -z "${SPARK_REPO}" ]]; then
    SPARK_REPO="$(mktemp -d -t spark-build-XXXXXX)"
    KEEP_CLONE=false
    log "Cloning buildonspark/spark into ${SPARK_REPO} (shallow)..."
    git clone --depth 1 https://github.com/buildonspark/spark.git "${SPARK_REPO}"
else
    if [[ ! -d "${SPARK_REPO}/signer" ]]; then
        err "Expected ${SPARK_REPO}/signer to exist — this does not look like the buildonspark/spark repo."
        exit 1
    fi
    log "Using existing Spark checkout at ${SPARK_REPO}"
fi

SIGNER_DIR="${SPARK_REPO}/signer"

# --- Build each target ------------------------------------------------------

for target in "${TARGETS[@]}"; do
    rid="$(target_to_rid "${target}")"
    log "Building spark-frost-uniffi for ${target} (-> ${rid})"
    (
        cd "${SIGNER_DIR}"
        cargo xwin build --release -p spark-frost-uniffi --target "${target}"
    )

    artifact="${SIGNER_DIR}/target/${target}/release/spark_frost.dll"
    if [[ ! -f "${artifact}" ]]; then
        err "Build succeeded but ${artifact} is missing — check the workspace members list in signer/Cargo.toml."
        exit 1
    fi

    dest_dir="${REPO_ROOT}/src/NSpark/runtimes/${rid}/native"
    mkdir -p "${dest_dir}"
    cp "${artifact}" "${dest_dir}/spark_frost.dll"

    size=$(wc -c <"${dest_dir}/spark_frost.dll" | tr -d ' ')
    sha=$(shasum -a 256 "${dest_dir}/spark_frost.dll" | awk '{print $1}')
    log "  -> ${dest_dir}/spark_frost.dll (${size} bytes, sha256=${sha})"
done

# --- Cleanup ---------------------------------------------------------------

if [[ "${KEEP_CLONE}" == false ]]; then
    log "Removing temporary clone at ${SPARK_REPO}"
    rm -rf "${SPARK_REPO}"
fi

log "Done. Verify the DLLs work end-to-end:"
log "  dotnet test tests/NSpark.IntegrationTests/NSpark.IntegrationTests.csproj --framework net8.0"
log ""
log "Next step: Authenticode-sign the DLLs before shipping. See docs/native-build.md."
