#!/usr/bin/env bash
# download-llvm-native.sh
# Installs libLLVM shared library for Linux via apt.
#
# Works on:
#   - Native Linux x86_64 and aarch64 (CI or dev machine)
#   - Cross-architecture download (e.g. arm64 .so on x64 host)
#   - WSL on Windows (for Windows devs who need the Linux .so)
#
# When running under WSL the script detects the Windows-side repo path and
# copies (not symlinks) libLLVM-<major>.so into runtimes/linux-{x64,arm64}/libLLVM.so
# so that dotnet on Windows can include it in cross-builds.
#
# Usage:
#   ./scripts/download-llvm-native.sh              # default LLVM major = 22, auto-detect arch
#   ./scripts/download-llvm-native.sh 23            # specify a different major
#   ./scripts/download-llvm-native.sh 22 arm64      # cross-download arm64 .so on x64 host
#   ./scripts/download-llvm-native.sh 22 x64        # cross-download x64 .so on arm64 host
#
# Prerequisites: apt / sudo, wget (for LLVM apt repo key)

set -euo pipefail

LLVM_MAJOR="${1:-22}"
TARGET_ARCH="${2:-}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIB_DIR="$REPO_ROOT/runtimes"

# ── Resolve target architecture ──────────────────────────────────────────
resolve_arch() {
    local arch="$1"
    case "$arch" in
        x86_64|amd64|x64)
            echo "x64"
            ;;
        aarch64|arm64)
            echo "arm64"
            ;;
        *)
            echo "ERROR: Unsupported architecture: $arch" >&2
            exit 1
            ;;
    esac
}

HOST_ARCH="$(uname -m)"
HOST_NORMALIZED=$(resolve_arch "$HOST_ARCH")

if [ -n "$TARGET_ARCH" ]; then
    TARGET_NORMALIZED=$(resolve_arch "$TARGET_ARCH")
else
    TARGET_NORMALIZED="$HOST_NORMALIZED"
fi

case "$TARGET_NORMALIZED" in
    x64)
        RID="linux-x64"
        LIB_ARCH_DIR="x86_64-linux-gnu"
        DEB_ARCH="amd64"
        ;;
    arm64)
        RID="linux-arm64"
        LIB_ARCH_DIR="aarch64-linux-gnu"
        DEB_ARCH="arm64"
        ;;
esac

CROSS_MODE=false
if [ "$TARGET_NORMALIZED" != "$HOST_NORMALIZED" ]; then
    CROSS_MODE=true
fi

if [ "$CROSS_MODE" = true ]; then
    # ── Cross-architecture download via apt multiarch ────────────────────
    echo ""
    echo "=== Downloading LLVM ${LLVM_MAJOR} shared library for $RID (cross from $HOST_NORMALIZED) ==="

    CODENAME=$(lsb_release -cs 2>/dev/null || echo "noble")

    sudo dpkg --add-architecture "$DEB_ARCH"

    # Add architecture-specific package sources
    if [ "$DEB_ARCH" = "arm64" ]; then
        PORTS_URL="http://ports.ubuntu.com/ubuntu-ports"
    else
        PORTS_URL="http://archive.ubuntu.com/ubuntu"
    fi
    echo "deb [arch=${DEB_ARCH}] ${PORTS_URL} ${CODENAME} main universe" \
        | sudo tee /etc/apt/sources.list.d/${DEB_ARCH}-ports.list
    echo "deb [arch=${DEB_ARCH}] ${PORTS_URL} ${CODENAME}-updates main universe" \
        | sudo tee -a /etc/apt/sources.list.d/${DEB_ARCH}-ports.list

    # Add LLVM apt repo for the target architecture
    wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
        | gpg --dearmor | sudo tee /usr/share/keyrings/llvm-archive-keyring-${DEB_ARCH}.gpg > /dev/null
    echo "deb [arch=${DEB_ARCH},signed-by=/usr/share/keyrings/llvm-archive-keyring-${DEB_ARCH}.gpg] https://apt.llvm.org/${CODENAME}/ llvm-toolchain-${CODENAME}-${LLVM_MAJOR} main" \
        | sudo tee /etc/apt/sources.list.d/llvm-${LLVM_MAJOR}-${DEB_ARCH}.list

    # Update may warn about repos that don't carry the new arch — that's expected
    sudo apt-get update -qq 2>/dev/null || true

    # Download and extract the .deb (don't install — wrong arch for host)
    TMPDIR=$(mktemp -d)
    cd "$TMPDIR"
    apt-get download "libllvm${LLVM_MAJOR}:${DEB_ARCH}"
    dpkg-deb -x libllvm${LLVM_MAJOR}_*.deb extracted/

    # Locate the .so inside the extracted package
    SO_PATH=""
    for candidate in \
        "extracted/usr/lib/${LIB_ARCH_DIR}/libLLVM-${LLVM_MAJOR}.so" \
        extracted/usr/lib/${LIB_ARCH_DIR}/libLLVM.so.${LLVM_MAJOR}* \
        extracted/usr/lib/${LIB_ARCH_DIR}/libLLVM-${LLVM_MAJOR}.so.*; do
        if [ -f "$candidate" ]; then
            SO_PATH="$candidate"
            break
        fi
    done

    if [ -z "$SO_PATH" ]; then
        echo "ERROR: libLLVM for ${DEB_ARCH} not found in extracted .deb" >&2
        ls -la "extracted/usr/lib/${LIB_ARCH_DIR}/" 2>/dev/null || true
        exit 1
    fi

    echo "  -> $SO_PATH ($(du -h "$SO_PATH" | cut -f1))"

    LINUX_OUT="$LIB_DIR/$RID"
    mkdir -p "$LINUX_OUT"
    cp -f "$SO_PATH" "$LINUX_OUT/libLLVM.so"
    echo "  -> Copied to $LINUX_OUT/libLLVM.so"

    rm -rf "$TMPDIR"
else
    # ── Native install via apt ───────────────────────────────────────────
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

    # ── Locate the installed .so ─────────────────────────────────────────
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

    # ── Place the library where the csproj expects it ────────────────────
    LINUX_OUT="$LIB_DIR/$RID"
    mkdir -p "$LINUX_OUT"

    # Under WSL, symlinks into /usr/lib won't be visible from the Windows side,
    # so we always copy the file to be safe in both native Linux and WSL scenarios.
    cp -f "$SO_PATH" "$LINUX_OUT/libLLVM.so"
    echo "  -> Copied to $LINUX_OUT/libLLVM.so"

    # ── WSL hint ─────────────────────────────────────────────────────────
    if grep -qi microsoft /proc/version 2>/dev/null; then
        echo ""
        echo "  (WSL detected — file was copied, not symlinked, so it is"
        echo "   accessible from the Windows side via the repo's runtimes/ directory.)"
    fi
fi

echo ""
echo "=== Done (LLVM ${LLVM_MAJOR}, $RID) ==="
