#!/usr/bin/env bash
# download-llvm-native.sh
# Provisions LLVM native libraries for Ashes development on Linux (and CI).
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
#   ./scripts/download-llvm-native.sh --all         # download all three: linux-x64, linux-arm64, win-x64
#   ./scripts/download-llvm-native.sh --all 22.1.2  # specify full LLVM version for Windows DLL
#
# Prerequisites: apt / sudo, wget (for LLVM apt repo key), tar (for Windows DLL)

set -euo pipefail

ALL_MODE=false
LLVM_MAJOR="22"
LLVM_FULL_VERSION="22.1.2"
TARGET_ARCH=""

if [ "${1:-}" = "--all" ] || [ "${1:-}" = "-a" ]; then
    ALL_MODE=true
    if [ -n "${2:-}" ]; then
        LLVM_FULL_VERSION="$2"
        LLVM_MAJOR="${LLVM_FULL_VERSION%%.*}"
    fi
else
    LLVM_MAJOR="${1:-22}"
    TARGET_ARCH="${2:-}"
fi

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

# ── Helper: install native Linux .so via apt ─────────────────────────────
install_linux_native() {
    local llvm_major="$1"
    local target_normalized="$2"
    local rid lib_arch_dir

    case "$target_normalized" in
        x64) rid="linux-x64"; lib_arch_dir="x86_64-linux-gnu" ;;
        arm64) rid="linux-arm64"; lib_arch_dir="aarch64-linux-gnu" ;;
    esac

    echo ""
    echo "=== Installing LLVM ${llvm_major} shared library via apt ($rid) ==="

    if ! apt-cache show "libllvm${llvm_major}" &>/dev/null; then
        echo "Adding LLVM apt repository..."
        wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
            | gpg --dearmor | sudo tee /usr/share/keyrings/llvm-archive-keyring.gpg > /dev/null
        local codename
        codename=$(lsb_release -cs 2>/dev/null || echo "noble")
        echo "deb [signed-by=/usr/share/keyrings/llvm-archive-keyring.gpg] https://apt.llvm.org/${codename}/ llvm-toolchain-${codename}-${llvm_major} main" \
            | sudo tee /etc/apt/sources.list.d/llvm-${llvm_major}.list
        sudo apt-get update -qq
    fi

    sudo apt-get install -y -qq "libllvm${llvm_major}"

    local so_path
    so_path=$(ldconfig -p | grep -E "libLLVM[-.]${llvm_major}[.]so|libLLVM[.]so[.]${llvm_major}" | awk '{print $NF}' | head -1 || true)
    if [ -z "$so_path" ]; then
        for candidate in \
            "/usr/lib/${lib_arch_dir}/libLLVM-${llvm_major}.so" \
            "/usr/lib/${lib_arch_dir}/libLLVM.so.${llvm_major}"*; do
            if [ -f "$candidate" ]; then
                so_path="$candidate"
                break
            fi
        done
    fi

    if [ ! -f "$so_path" ]; then
        echo "ERROR: libLLVM-${llvm_major}.so not found after install" >&2
        exit 1
    fi

    echo "  -> $so_path ($(du -h "$so_path" | cut -f1))"

    local linux_out="$LIB_DIR/$rid"
    mkdir -p "$linux_out"
    cp -f "$so_path" "$linux_out/libLLVM.so"
    echo "  -> Copied to $linux_out/libLLVM.so"

    if grep -qi microsoft /proc/version 2>/dev/null; then
        echo ""
        echo "  (WSL detected — file was copied, not symlinked, so it is"
        echo "   accessible from the Windows side via the repo's runtimes/ directory.)"
    fi
}

# ── Helper: cross-download Linux .so via apt multiarch ───────────────────
download_linux_cross() {
    local llvm_major="$1"
    local target_normalized="$2"
    local rid lib_arch_dir deb_arch

    case "$target_normalized" in
        x64) rid="linux-x64"; lib_arch_dir="x86_64-linux-gnu"; deb_arch="amd64" ;;
        arm64) rid="linux-arm64"; lib_arch_dir="aarch64-linux-gnu"; deb_arch="arm64" ;;
    esac

    echo ""
    echo "=== Downloading LLVM ${llvm_major} shared library for $rid (cross from $HOST_NORMALIZED) ==="

    local codename
    codename=$(lsb_release -cs 2>/dev/null || echo "noble")

    sudo dpkg --add-architecture "$deb_arch"

    local ports_url
    if [ "$deb_arch" = "arm64" ]; then
        ports_url="https://ports.ubuntu.com/ubuntu-ports"
    else
        ports_url="https://archive.ubuntu.com/ubuntu"
    fi
    echo "deb [arch=${deb_arch}] ${ports_url} ${codename} main universe" \
        | sudo tee /etc/apt/sources.list.d/${deb_arch}-ports.list
    echo "deb [arch=${deb_arch}] ${ports_url} ${codename}-updates main universe" \
        | sudo tee -a /etc/apt/sources.list.d/${deb_arch}-ports.list

    wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
        | gpg --dearmor | sudo tee /usr/share/keyrings/llvm-archive-keyring-${deb_arch}.gpg > /dev/null
    echo "deb [arch=${deb_arch},signed-by=/usr/share/keyrings/llvm-archive-keyring-${deb_arch}.gpg] https://apt.llvm.org/${codename}/ llvm-toolchain-${codename}-${llvm_major} main" \
        | sudo tee /etc/apt/sources.list.d/llvm-${llvm_major}-${deb_arch}.list

    if ! sudo apt-get update -qq 2>/tmp/apt-update-cross.log; then
        echo "  (apt-get update reported warnings — this is expected when adding a cross-arch; check /tmp/apt-update-cross.log if the download below fails)"
    fi

    local tmpdir
    tmpdir=$(mktemp -d)
    cd "$tmpdir"
    apt-get download "libllvm${llvm_major}:${deb_arch}"
    dpkg-deb -x libllvm${llvm_major}_*.deb extracted/

    local so_path=""
    for candidate in \
        "extracted/usr/lib/${lib_arch_dir}/libLLVM-${llvm_major}.so" \
        extracted/usr/lib/${lib_arch_dir}/libLLVM.so.${llvm_major}* \
        extracted/usr/lib/${lib_arch_dir}/libLLVM-${llvm_major}.so.*; do
        if [ -f "$candidate" ]; then
            so_path="$candidate"
            break
        fi
    done

    if [ -z "$so_path" ]; then
        echo "ERROR: libLLVM for ${deb_arch} not found in extracted .deb." >&2
        echo "  This may indicate the package structure has changed or DEB_ARCH='${deb_arch}' is incorrect." >&2
        echo "  Expected path: extracted/usr/lib/${lib_arch_dir}/libLLVM-${llvm_major}.so (or similar)" >&2
        ls -la "extracted/usr/lib/${lib_arch_dir}/" 2>/dev/null || true
        exit 1
    fi

    echo "  -> $so_path ($(du -h "$so_path" | cut -f1))"

    local linux_out="$LIB_DIR/$rid"
    mkdir -p "$linux_out"
    cp -f "$so_path" "$linux_out/libLLVM.so"
    echo "  -> Copied to $linux_out/libLLVM.so"

    rm -rf "$tmpdir"
}

# ── Helper: download a single Linux .so (native or cross as needed) ──────
download_linux() {
    local llvm_major="$1"
    local target_normalized="$2"

    if [ "$target_normalized" = "$HOST_NORMALIZED" ]; then
        install_linux_native "$llvm_major" "$target_normalized"
    else
        download_linux_cross "$llvm_major" "$target_normalized"
    fi
}

# ── Helper: download Windows DLL from GitHub release ─────────────────────
download_windows_dll() {
    local llvm_version="$1"

    echo ""
    echo "=== Downloading LLVM ${llvm_version} Windows x64 DLL ==="

    local win_url="https://github.com/llvm/llvm-project/releases/download/llvmorg-${llvm_version}/clang+llvm-${llvm_version}-x86_64-pc-windows-msvc.tar.xz"
    local win_out="$LIB_DIR/win-x64"
    mkdir -p "$win_out"

    local tmpdir
    tmpdir=$(mktemp -d)

    echo "  Downloading from $win_url ..."
    wget -q --show-progress -O "$tmpdir/llvm-win.tar.xz" "$win_url"

    echo "  Extracting LLVM-C.dll..."
    mkdir -p "$tmpdir/win"
    tar -xf "$tmpdir/llvm-win.tar.xz" -C "$tmpdir/win"

    local llvm_c_dll
    llvm_c_dll=$(find "$tmpdir/win" -name 'LLVM-C.dll' -print -quit)
    if [ -z "$llvm_c_dll" ]; then
        echo "ERROR: Could not find LLVM-C.dll in Windows archive" >&2
        rm -rf "$tmpdir"
        exit 1
    fi

    cp -f "$llvm_c_dll" "$win_out/libLLVM.dll"
    local size
    size=$(du -h "$win_out/libLLVM.dll" | cut -f1)
    echo "  -> $win_out/libLLVM.dll ($size)"

    rm -rf "$tmpdir"
}

# ── Main ─────────────────────────────────────────────────────────────────
if [ "$ALL_MODE" = true ]; then
    # Download all three runtimes: linux-x64, linux-arm64, win-x64
    download_linux "$LLVM_MAJOR" "x64"
    download_linux "$LLVM_MAJOR" "arm64"
    download_windows_dll "$LLVM_FULL_VERSION"

    echo ""
    echo "=== Done (LLVM ${LLVM_MAJOR}, all runtimes: linux-x64, linux-arm64, win-x64) ==="
else
    # Single-target mode (original behavior)
    if [ -n "$TARGET_ARCH" ]; then
        TARGET_NORMALIZED=$(resolve_arch "$TARGET_ARCH")
    else
        TARGET_NORMALIZED="$HOST_NORMALIZED"
    fi

    download_linux "$LLVM_MAJOR" "$TARGET_NORMALIZED"

    local_rid=""
    case "$TARGET_NORMALIZED" in
        x64) local_rid="linux-x64" ;;
        arm64) local_rid="linux-arm64" ;;
    esac

    echo ""
    echo "=== Done (LLVM ${LLVM_MAJOR}, $local_rid) ==="
fi
