#!/usr/bin/env bash
# download-llvm-native.sh
# Provisions LLVM native libraries for Ashes development/CI.
#
# Linux:   installs libLLVM-<major>.so via apt (the official LLVM 22+ Linux
#          release only ships static .a libraries, so apt is the simplest way
#          to get the shared library).
# Windows: downloads LLVM-C.dll from the official LLVM GitHub release and
#          renames it to libLLVM.dll to match the DllImport name.
#
# Usage:
#   ./scripts/download-llvm-native.sh              # default LLVM major = 22
#   ./scripts/download-llvm-native.sh 23           # specify a different major
#
# Prerequisites:
#   Linux  – apt / sudo, wget (for apt repo key)
#   Windows (Git Bash / WSL) – curl, tar

set -euo pipefail

LLVM_MAJOR="${1:-22}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIB_DIR="$REPO_ROOT/lib/Ashes"

# ── Linux ─────────────────────────────────────────────────────────────────────
install_linux() {
    echo ""
    echo "=== Installing LLVM ${LLVM_MAJOR} shared library via apt ==="

    # Add the official LLVM apt repository if not already present
    if ! apt-cache show "libllvm${LLVM_MAJOR}" &>/dev/null; then
        echo "Adding LLVM apt repository..."
        wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key | sudo apt-key add -
        # Detect Ubuntu codename (e.g. jammy, noble)
        CODENAME=$(lsb_release -cs 2>/dev/null || echo "noble")
        echo "deb http://apt.llvm.org/${CODENAME}/ llvm-toolchain-${CODENAME}-${LLVM_MAJOR} main" \
            | sudo tee /etc/apt/sources.list.d/llvm-${LLVM_MAJOR}.list
        sudo apt-get update -qq
    fi

    sudo apt-get install -y -qq "libllvm${LLVM_MAJOR}"

    # Verify the shared library exists
    SO_PATH=$(ldconfig -p | grep "libLLVM-${LLVM_MAJOR}.so" | awk '{print $NF}' | head -1)
    if [ -z "$SO_PATH" ]; then
        SO_PATH="/usr/lib/x86_64-linux-gnu/libLLVM-${LLVM_MAJOR}.so"
    fi

    if [ ! -f "$SO_PATH" ]; then
        echo "ERROR: libLLVM-${LLVM_MAJOR}.so not found after install" >&2
        exit 1
    fi

    echo "  -> $SO_PATH ($(du -h "$SO_PATH" | cut -f1))"

    # Symlink into lib/Ashes/linux-x64/ so the csproj can copy it to build output
    LINUX_OUT="$LIB_DIR/linux-x64"
    mkdir -p "$LINUX_OUT"
    ln -sf "$SO_PATH" "$LINUX_OUT/libLLVM.so"
    echo "  -> Symlinked to $LINUX_OUT/libLLVM.so"
}

# ── Windows (LLVM-C.dll from official release) ───────────────────────────────
install_windows_dll() {
    # Determine the latest patch version for this major.
    # Default to .1.2 which is a common patch; override with LLVM_VERSION env var.
    LLVM_VERSION="${LLVM_VERSION:-${LLVM_MAJOR}.1.2}"

    echo ""
    echo "=== Downloading LLVM ${LLVM_VERSION} Windows x64 (LLVM-C.dll) ==="

    TMP_DIR="$(mktemp -d)"
    cleanup() { rm -rf "$TMP_DIR"; }
    trap cleanup EXIT

    WIN_URL="https://github.com/llvm/llvm-project/releases/download/llvmorg-${LLVM_VERSION}/clang+llvm-${LLVM_VERSION}-x86_64-pc-windows-msvc.tar.xz"
    WIN_OUT="$LIB_DIR/win-x64"
    mkdir -p "$WIN_OUT"

    curl -fSL --progress-bar "$WIN_URL" -o "$TMP_DIR/llvm-win.tar.xz"

    echo "Extracting LLVM-C.dll..."
    tar -xf "$TMP_DIR/llvm-win.tar.xz" -C "$TMP_DIR" --wildcards "*/bin/LLVM-C.dll"

    WIN_DLL=$(find "$TMP_DIR" -name "LLVM-C.dll" -type f | head -1)
    if [ -z "$WIN_DLL" ]; then
        echo "ERROR: Could not find LLVM-C.dll in Windows archive" >&2
        exit 1
    fi

    # Rename to libLLVM.dll to match the DllImport name ("libLLVM")
    cp "$WIN_DLL" "$WIN_OUT/libLLVM.dll"
    echo "  -> $WIN_OUT/libLLVM.dll ($(du -h "$WIN_OUT/libLLVM.dll" | cut -f1))"
}

# ── Dispatch ──────────────────────────────────────────────────────────────────
case "$(uname -s)" in
    Linux*)
        install_linux
        ;;
    MINGW*|MSYS*|CYGWIN*)
        install_windows_dll
        ;;
    *)
        echo "Unsupported OS: $(uname -s)" >&2
        echo "On Linux, run this script directly."
        echo "On Windows, use scripts/download-llvm-native.ps1 instead."
        exit 1
        ;;
esac

echo ""
echo "=== Done (LLVM ${LLVM_MAJOR}) ==="
