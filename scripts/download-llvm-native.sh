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
# Prerequisites: apt, root access (directly or via sudo), wget (for LLVM apt repo key), tar (for Windows DLL)

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

if [ "$(id -u)" -eq 0 ]; then
    SUDO=()
elif command -v sudo >/dev/null 2>&1; then
    SUDO=(sudo)
else
    echo "ERROR: This script requires root privileges. Run it as root or install sudo." >&2
    exit 1
fi

as_root() {
    if [ "${#SUDO[@]}" -eq 0 ]; then
        "$@"
    else
        "${SUDO[@]}" "$@"
    fi
}

ensure_command() {
    local command_name="$1"
    local package_name="$2"

    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Installing missing prerequisite: $package_name"
        as_root apt-get install -y -qq "$package_name"
    fi
}

require_command() {
    local command_name="$1"
    local hint="${2:-}"

    if ! command -v "$command_name" >/dev/null 2>&1; then
        if [ -n "$hint" ]; then
            echo "ERROR: Required command '$command_name' is missing. $hint" >&2
        else
            echo "ERROR: Required command '$command_name' is missing." >&2
        fi
        exit 1
    fi
}

is_valid_codename() {
    local codename="$1"
    case "$codename" in
        ""|n/a|N/A|na|NA|rolling|arch|cachyos|unknown)
            return 1
            ;;
        *)
            return 0
            ;;
    esac
}

resolve_llvm_apt_codename() {
    local llvm_major="$1"

    local candidates=()

    local lsb_codename
    lsb_codename=$(lsb_release -cs 2>/dev/null || true)
    if is_valid_codename "$lsb_codename"; then
        candidates+=("$lsb_codename")
    fi

    if [ -r /etc/os-release ]; then
        # shellcheck disable=SC1091
        . /etc/os-release
        if is_valid_codename "${UBUNTU_CODENAME:-}"; then
            candidates+=("$UBUNTU_CODENAME")
        fi
        if is_valid_codename "${VERSION_CODENAME:-}"; then
            candidates+=("$VERSION_CODENAME")
        fi
    fi

    # Known apt.llvm.org suites likely to exist for LLVM 22.
    candidates+=("noble" "jammy" "bookworm" "bullseye")

    local seen=""
    local codename
    for codename in "${candidates[@]}"; do
        case " $seen " in
            *" $codename "*)
                continue
                ;;
        esac
        seen="$seen $codename"

        local suite="llvm-toolchain-${codename}-${llvm_major}"
        local url_plain="https://apt.llvm.org/${codename}/dists/${suite}/main/binary-amd64/Packages"
        local url_gz="${url_plain}.gz"

        if wget -q --spider "$url_plain" || wget -q --spider "$url_gz"; then
            echo "$codename"
            return
        fi
    done

    echo "ERROR: Could not resolve a valid apt.llvm.org codename for LLVM ${llvm_major}." >&2
    exit 1
}

detect_package_manager() {
    if command -v apt-get >/dev/null 2>&1; then
        echo "apt"
        return
    fi

    if command -v pacman >/dev/null 2>&1; then
        echo "pacman"
        return
    fi

    echo "ERROR: Unsupported package manager. Install apt-get or pacman." >&2
    exit 1
}

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
PACKAGE_MANAGER="$(detect_package_manager)"

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
    echo "=== Installing LLVM ${llvm_major} shared library ($rid) ==="

    if [ "$PACKAGE_MANAGER" = "apt" ]; then
        if ! apt-cache show "libllvm${llvm_major}" &>/dev/null; then
            echo "Adding LLVM apt repository..."
            as_root mkdir -p /usr/share/keyrings /etc/apt/sources.list.d
            wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
                | gpg --dearmor | as_root tee /usr/share/keyrings/llvm-archive-keyring.gpg > /dev/null
            local codename
            codename=$(resolve_llvm_apt_codename "$llvm_major")
            echo "deb [signed-by=/usr/share/keyrings/llvm-archive-keyring.gpg] https://apt.llvm.org/${codename}/ llvm-toolchain-${codename}-${llvm_major} main" \
                | as_root tee /etc/apt/sources.list.d/llvm-${llvm_major}.list
            as_root apt-get update -qq
        fi

        as_root apt-get install -y -qq "libllvm${llvm_major}"
    elif [ "$PACKAGE_MANAGER" = "pacman" ]; then
        echo "Installing LLVM shared library via pacman..."
        as_root pacman -Sy --noconfirm --needed llvm-libs
    else
        echo "ERROR: Unsupported package manager: $PACKAGE_MANAGER" >&2
        exit 1
    fi

    local so_path
    so_path=$(ldconfig -p | grep -E "libLLVM[-.]${llvm_major}[.]so|libLLVM[.]so[.]${llvm_major}" | awk '{print $NF}' | head -1 || true)
    if [ -z "$so_path" ]; then
        for candidate in \
            "/usr/lib/${lib_arch_dir}/libLLVM-${llvm_major}.so" \
            "/usr/lib/${lib_arch_dir}/libLLVM.so.${llvm_major}"* \
            "/usr/lib/libLLVM-${llvm_major}.so" \
            "/usr/lib/libLLVM.so.${llvm_major}"*; do
            if [ -f "$candidate" ]; then
                so_path="$candidate"
                break
            fi
        done
    fi

    if [ -z "$so_path" ] && [ "$PACKAGE_MANAGER" = "pacman" ]; then
        so_path=$(ls -1 /usr/lib/libLLVM-*.so 2>/dev/null | sort -V | tail -1 || true)
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
    codename=$(resolve_llvm_apt_codename "$llvm_major")

    if [ "$PACKAGE_MANAGER" != "apt" ]; then
        if [ "$target_normalized" = "arm64" ]; then
            download_linux_cross_from_llvm_release "$llvm_major" "$target_normalized"
            return
        fi

        echo "ERROR: Cross-architecture Linux download for $target_normalized is unsupported on pacman systems." >&2
        exit 1
    fi

    as_root dpkg --add-architecture "$deb_arch"
    as_root mkdir -p /usr/share/keyrings /etc/apt/sources.list.d

    local ports_url
    if [ "$deb_arch" = "arm64" ]; then
        ports_url="https://ports.ubuntu.com/ubuntu-ports"
    else
        ports_url="https://archive.ubuntu.com/ubuntu"
    fi
    echo "deb [arch=${deb_arch}] ${ports_url} ${codename} main universe" \
        | as_root tee /etc/apt/sources.list.d/${deb_arch}-ports.list
    echo "deb [arch=${deb_arch}] ${ports_url} ${codename}-updates main universe" \
        | as_root tee -a /etc/apt/sources.list.d/${deb_arch}-ports.list

    wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
        | gpg --dearmor | as_root tee /usr/share/keyrings/llvm-archive-keyring-${deb_arch}.gpg > /dev/null
    echo "deb [arch=${deb_arch},signed-by=/usr/share/keyrings/llvm-archive-keyring-${deb_arch}.gpg] https://apt.llvm.org/${codename}/ llvm-toolchain-${codename}-${llvm_major} main" \
        | as_root tee /etc/apt/sources.list.d/llvm-${llvm_major}-${deb_arch}.list

    if ! as_root apt-get update -qq 2>/tmp/apt-update-cross.log; then
        echo "  (apt-get update reported warnings — this is expected when adding a cross-arch; check /tmp/apt-update-cross.log if the download below fails)"
    fi

    local tmpdir
    tmpdir=$(mktemp -d)
    (
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
    )

    rm -rf "$tmpdir"
}

# ── Helper: cross-download Linux .so from apt.llvm.org packages (pacman hosts) ──
download_linux_cross_from_llvm_release() {
    local llvm_major="$1"
    local target_normalized="$2"
    local rid

    case "$target_normalized" in
        arm64) rid="linux-arm64" ;;
        *)
            echo "ERROR: LLVM release cross-download only supports arm64 target currently." >&2
            exit 1
            ;;
    esac

    echo ""
    echo "=== Downloading LLVM ${llvm_major} shared library for $rid from apt.llvm.org package index ==="

    require_command wget "Install wget and retry."
    require_command ar "Install binutils and retry."
    require_command tar "Install tar and retry."

    local codename
    codename=$(resolve_llvm_apt_codename "$llvm_major")

    local suite="llvm-toolchain-${codename}-${llvm_major}"
    local base_url="https://apt.llvm.org/${codename}"
    local index_plain_url="${base_url}/dists/${suite}/main/binary-arm64/Packages"
    local index_gz_url="${index_plain_url}.gz"

    local tmpdir
    tmpdir=$(mktemp -d)

    local index_file="$tmpdir/Packages"
    if wget -qO "$index_file" "$index_plain_url"; then
        :
    elif wget -qO "$tmpdir/Packages.gz" "$index_gz_url"; then
        require_command gzip "Install gzip and retry."
        gzip -dc "$tmpdir/Packages.gz" > "$index_file"
    else
        echo "ERROR: Could not download package index from apt.llvm.org for ${suite} (arm64)." >&2
        rm -rf "$tmpdir"
        exit 1
    fi

    local deb_rel_path
    deb_rel_path=$(awk -v pkg="libllvm${llvm_major}" '
        $1 == "Package:" { in_pkg = ($2 == pkg) }
        in_pkg && $1 == "Filename:" { print $2; exit }
    ' "$index_file")

    if [ -z "$deb_rel_path" ]; then
        echo "ERROR: Could not locate package entry for libllvm${llvm_major} in apt.llvm.org index." >&2
        echo "  Tried suite: ${suite} (arm64)" >&2
        rm -rf "$tmpdir"
        exit 1
    fi

    local deb_url="${base_url}/${deb_rel_path}"
    local deb_file="$tmpdir/libllvm${llvm_major}-arm64.deb"

    echo "  Downloading from $deb_url ..."
    wget -q --show-progress -O "$deb_file" "$deb_url"

    local data_member
    data_member=$(ar t "$deb_file" | grep -E '^data\.tar\.(xz|gz|zst)$' | head -1 || true)
    if [ -z "$data_member" ]; then
        echo "ERROR: Could not find data.tar.* member in downloaded .deb" >&2
        rm -rf "$tmpdir"
        exit 1
    fi

    local data_tar="$tmpdir/$data_member"
    ar p "$deb_file" "$data_member" > "$data_tar"

    local extract_dir="$tmpdir/extracted"
    mkdir -p "$extract_dir"

    case "$data_member" in
        *.tar.xz)
            tar -xJf "$data_tar" -C "$extract_dir"
            ;;
        *.tar.gz)
            tar -xzf "$data_tar" -C "$extract_dir"
            ;;
        *.tar.zst)
            tar --zstd -xf "$data_tar" -C "$extract_dir"
            ;;
        *)
            echo "ERROR: Unsupported data archive format: $data_member" >&2
            rm -rf "$tmpdir"
            exit 1
            ;;
    esac

    local so_path
    so_path=$(find "$extract_dir" -type f -name 'libLLVM.so*' | sort -V | tail -1 || true)
    if [ -z "$so_path" ]; then
        echo "ERROR: libLLVM.so not found in extracted package payload" >&2
        rm -rf "$tmpdir"
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

    ensure_command xz xz-utils

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
