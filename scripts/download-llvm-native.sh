#!/usr/bin/env bash
# download-llvm-native.sh
# Installs libLLVM shared library for Linux via apt.
#
# Works on:
#   - Native Linux x86_64 and aarch64 (CI or dev machine)
#   - WSL on Windows (for Windows devs who need the Linux .so)
#
# When running under WSL the script detects the Windows-side repo path and
# copies (not symlinks) libLLVM-<major>.so into runtimes/linux-{x64,arm64}/libLLVM.so
# so that dotnet on Windows can include it in cross-builds.
#
# Usage:
#   ./scripts/download-llvm-native.sh          # default LLVM major = 22
#   ./scripts/download-llvm-native.sh 23       # specify a different major
#
# Prerequisites: apt / sudo, wget (for LLVM apt repo key)

set -euo pipefail

LLVM_MAJOR="${1:-22}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIB_DIR="$REPO_ROOT/runtimes"

# ── Detect architecture ──────────────────────────────────────────────────
HOST_ARCH="$(uname -m)"
case "$HOST_ARCH" in
    x86_64|amd64)
        RID="linux-x64"
        LIB_ARCH_DIR="x86_64-linux-gnu"
        ;;
    aarch64|arm64)
        RID="linux-arm64"
        LIB_ARCH_DIR="aarch64-linux-gnu"
        ;;
    *)
        echo "ERROR: Unsupported architecture: $HOST_ARCH" >&2
        exit 1
        ;;
esac

# ── Install libLLVM via apt ──────────────────────────────────────────────────
echo ""
echo "=== Installing LLVM ${LLVM_MAJOR} shared library via apt ($RID) ==="

# Add the official LLVM apt repository if the package is not already available
if ! apt-cache show "libllvm${LLVM_MAJOR}" &>/dev/null; then
    echo "Adding LLVM apt repository..."
    wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
        | gpg --dearmor | sudo tee /usr/share/keyrings/llvm-archive-keyring.gpg > /dev/null
    # Detect Ubuntu codename (e.g. jammy, noble)
    CODENAME=$(lsb_release -cs 2>/dev/null || echo "noble")
    echo "deb [signed-by=/usr/share/keyrings/llvm-archive-keyring.gpg] https://apt.llvm.org/${CODENAME}/ llvm-toolchain-${CODENAME}-${LLVM_MAJOR} main" \
        | sudo tee /etc/apt/sources.list.d/llvm-${LLVM_MAJOR}.list
    sudo apt-get update -qq
fi

sudo apt-get install -y -qq "libllvm${LLVM_MAJOR}"

# ── Locate the installed .so ─────────────────────────────────────────────
# Newer LLVM versions use libLLVM.so.<major>.<minor> naming; older versions
# use libLLVM-<major>.so.  Try both patterns via ldconfig, then fall back to
# the well-known filesystem paths.
SO_PATH=$(ldconfig -p | grep -E "libLLVM[-.]${LLVM_MAJOR}[.]so|libLLVM[.]so[.]${LLVM_MAJOR}" | awk '{print $NF}' | head -1 || true)
if [ -z "$SO_PATH" ]; then
    # Filesystem fallback (covers both naming conventions)
    for candidate in \
        "/usr/lib/${LIB_ARCH_DIR}/libLLVM-${LLVM_MAJOR}.so" \
        "/usr/lib/${LIB_ARCH_DIR}/libLLVM.so.${LLVM_MAJOR}"*; do
        if [ -f "$candidate" ]; then
            SO_PATH="$candidate"
            break
        fi
    done
fi

if [ ! -f "$SO_PATH" ]; then
    echo "ERROR: libLLVM-${LLVM_MAJOR}.so not found after install" >&2
    exit 1
fi

echo "  -> $SO_PATH ($(du -h "$SO_PATH" | cut -f1))"

# ── Place the library where the csproj expects it ────────────────────────────
LINUX_OUT="$LIB_DIR/$RID"
mkdir -p "$LINUX_OUT"

# Under WSL, symlinks into /usr/lib won't be visible from the Windows side,
# so we always copy the file to be safe in both native Linux and WSL scenarios.
cp -f "$SO_PATH" "$LINUX_OUT/libLLVM.so"
echo "  -> Copied to $LINUX_OUT/libLLVM.so"

# ── WSL hint ─────────────────────────────────────────────────────────────
if grep -qi microsoft /proc/version 2>/dev/null; then
    echo ""
    echo "  (WSL detected — file was copied, not symlinked, so it is"
    echo "   accessible from the Windows side via the repo's runtimes/ directory.)"
fi

echo ""
echo "=== Done (LLVM ${LLVM_MAJOR}, $RID) ==="
