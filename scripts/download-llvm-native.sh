#!/usr/bin/env bash
# download-llvm-native.sh
# Downloads LLVM native libraries from official LLVM GitHub releases and places
# them into lib/Ashes/{linux,win}-x64/ alongside the existing LLVM tool bundle.
# The publish scripts already copy the whole lib/ tree to the output.
#
# Usage:
#   ./scripts/download-llvm-native.sh              # uses default version 22.1.2
#   ./scripts/download-llvm-native.sh 22.1.3       # specify a different version
#
# Prerequisites: curl, tar (with xz support)

set -euo pipefail

LLVM_VERSION="${1:-22.1.2}"
LLVM_MAJOR="${LLVM_VERSION%%.*}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIB_DIR="$REPO_ROOT/lib/Ashes"
TMP_DIR="$(mktemp -d)"

cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT

LINUX_URL="https://github.com/llvm/llvm-project/releases/download/llvmorg-${LLVM_VERSION}/LLVM-${LLVM_VERSION}-Linux-X64.tar.xz"
WIN_URL="https://github.com/llvm/llvm-project/releases/download/llvmorg-${LLVM_VERSION}/clang+llvm-${LLVM_VERSION}-x86_64-pc-windows-msvc.tar.xz"

LINUX_OUT="$LIB_DIR/linux-x64"
WIN_OUT="$LIB_DIR/win-x64"
mkdir -p "$LINUX_OUT" "$WIN_OUT"

# ── Linux x64 ────────────────────────────────────────────────────────────────
echo ""
echo "=== Downloading LLVM ${LLVM_VERSION} Linux x64 ==="
curl -fSL --progress-bar "$LINUX_URL" -o "$TMP_DIR/llvm-linux.tar.xz"

echo "Extracting libLLVM..."
tar -xf "$TMP_DIR/llvm-linux.tar.xz" -C "$TMP_DIR" --wildcards "*/lib/libLLVM*"

# Find the real shared library (not a symlink)
LINUX_LIB=$(find "$TMP_DIR" -name "libLLVM-${LLVM_MAJOR}.so" -type f 2>/dev/null | head -1)
if [ -z "$LINUX_LIB" ]; then
    LINUX_LIB=$(find "$TMP_DIR" -name "libLLVM.so*" ! -type l 2>/dev/null | head -1)
fi
if [ -z "$LINUX_LIB" ]; then
    echo "ERROR: Could not find libLLVM shared library in Linux archive" >&2
    exit 1
fi

cp "$LINUX_LIB" "$LINUX_OUT/libLLVM.so"
echo "  -> $LINUX_OUT/libLLVM.so ($(du -h "$LINUX_OUT/libLLVM.so" | cut -f1))"

# ── Windows x64 ──────────────────────────────────────────────────────────────
echo ""
echo "=== Downloading LLVM ${LLVM_VERSION} Windows x64 ==="
curl -fSL --progress-bar "$WIN_URL" -o "$TMP_DIR/llvm-win.tar.xz"

echo "Extracting LLVM-C.dll..."
tar -xf "$TMP_DIR/llvm-win.tar.xz" -C "$TMP_DIR" --wildcards "*/bin/LLVM-C.dll"

WIN_DLL=$(find "$TMP_DIR" -name "LLVM-C.dll" -type f | head -1)
if [ -z "$WIN_DLL" ]; then
    echo "ERROR: Could not find LLVM-C.dll in Windows archive" >&2
    exit 1
fi

cp "$WIN_DLL" "$WIN_OUT/libLLVM.dll"
# Renamed from LLVM-C.dll to libLLVM.dll to match the DllImport name ("libLLVM")
# used by LLVMSharp and the future P/Invoke layer.
echo "  -> $WIN_OUT/libLLVM.dll ($(du -h "$WIN_OUT/libLLVM.dll" | cut -f1))"

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "=== Done (LLVM ${LLVM_VERSION}) ==="
echo "Native libraries installed into:"
echo "  $LINUX_OUT/libLLVM.so"
echo "  $WIN_OUT/libLLVM.dll"
echo ""
echo "These are copied to the build output by Ashes.Backend.csproj."
