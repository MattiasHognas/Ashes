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
# Every Linux .so (libLLVM itself and its six non-universal shared-library dependencies,
# see LLVM_DEPENDENCY_PACKAGES below) is downloaded directly from Debian-style package
# archives (apt.llvm.org, archive.ubuntu.com, ports.ubuntu.com) and extracted with `ar`/`tar`,
# regardless of the host's own OS or package manager — this script never runs `apt-get
# install`/`pacman -S` for any of them, so the vendored files are reproducible byte-for-byte
# from any dev machine or CI runner and are never tied to (or left stale by) whatever the
# local system's package manager happens to have installed. Running `ashes` — the vendored
# libLLVM.so and its dependencies are only ever loaded by the *compiler itself*, never by a
# compiled program — must not require anything from this script's own build-time toolchain
# (wget/ar/tar/patchelf) or any of these packages to be present system-wide; that is the whole
# point of vendoring them into runtimes/<target>/ with an $ORIGIN RPATH on libLLVM.so.
#
# Usage:
#   ./scripts/download-llvm-native.sh              # default LLVM major = 22, auto-detect arch
#   ./scripts/download-llvm-native.sh 23            # specify a different major
#   ./scripts/download-llvm-native.sh 22 arm64      # cross-download arm64 .so on x64 host
#   ./scripts/download-llvm-native.sh 22 x64        # cross-download x64 .so on arm64 host
#   ./scripts/download-llvm-native.sh --win-x64     # download Windows x64 DLL only
#   ./scripts/download-llvm-native.sh --win-x64 22.1.2
#   ./scripts/download-llvm-native.sh --win-arm64   # download Windows ARM64 DLL only
#   ./scripts/download-llvm-native.sh --win-arm64 22.1.2
#   ./scripts/download-llvm-native.sh --all         # download all four: linux-x64, linux-arm64, win-x64, win-arm64
#   ./scripts/download-llvm-native.sh --all 22.1.2  # specify full LLVM version for the Windows DLLs
#
# Prerequisites: wget, ar (binutils), tar, patchelf, root access (directly or via sudo, only to
# auto-install patchelf if it's missing)

set -euo pipefail

ALL_MODE=false
WIN_X64_MODE=false
WIN_ARM64_MODE=false
LLVM_MAJOR="22"
LLVM_FULL_VERSION="22.1.2"
TARGET_ARCH=""

if [ "${1:-}" = "--all" ] || [ "${1:-}" = "-a" ]; then
    ALL_MODE=true
    if [ -n "${2:-}" ]; then
        LLVM_FULL_VERSION="$2"
        LLVM_MAJOR="${LLVM_FULL_VERSION%%.*}"
    fi
elif [ "${1:-}" = "--win-x64" ]; then
    WIN_X64_MODE=true
    if [ -n "${2:-}" ]; then
        LLVM_FULL_VERSION="$2"
        LLVM_MAJOR="${LLVM_FULL_VERSION%%.*}"
    fi
elif [ "${1:-}" = "--win-arm64" ]; then
    WIN_ARM64_MODE=true
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

# libLLVM.so's six shared-library dependencies beyond the universal Linux baseline
# (libc/libm/libstdc++/libgcc_s/the dynamic loader itself, which every dynamically-linked
# ELF binary already assumes). Each is used only by an LLVM feature ordinary Ashes codegen
# never exercises (Z3 constraint solving, libedit command-line editing for lli/opt-style
# REPL tools, libxml2 coverage-format support), but the dynamic loader still requires every
# one to be openable at process startup regardless of whether its code ever runs. Confirmed
# against a clean `mcr.microsoft.com/dotnet/runtime-deps:10.0` image (ci/images/Containerfile.base
# only lists six of these, because `apt-get install`ing them there lets apt's own dependency
# resolver silently pull in the rest transitively — this script has no such resolver and must
# vendor the complete closure explicitly, one level of transitive dependency deeper than that
# Containerfile spells out) — a bare Linux host cannot be assumed to have any of them.
# "package:file[,file...]" — the Ubuntu binary package to fetch, and the exact runtime .so
# filename(s) to extract from it (most packages ship exactly one; libicu74 ships two: the
# actual code libxml2 links against, plus the data blob that code itself depends on).
LLVM_DEPENDENCY_PACKAGES=(
    "libffi8:libffi.so.8"
    "libedit2:libedit.so.2"
    "libz3-4:libz3.so.4"
    "zlib1g:libz.so.1"
    "libzstd1:libzstd.so.1"
    "libxml2:libxml2.so.2"
    "libicu74:libicuuc.so.74,libicudata.so.74"
    "liblzma5:liblzma.so.5"
    "libtinfo6:libtinfo.so.6"
    "libbsd0:libbsd.so.0"
    "libmd0:libmd.so.0"
)

if [ "$(id -u)" -eq 0 ]; then
    SUDO=()
elif command -v sudo >/dev/null 2>&1; then
    SUDO=(sudo)
else
    SUDO=()
fi

as_root() {
    if [ "${#SUDO[@]}" -eq 0 ]; then
        "$@"
    else
        "${SUDO[@]}" "$@"
    fi
}

# Installs $2 (a package name) via whichever of apt/pacman is present, only if $1 (a command
# name) isn't already on PATH. Used solely for build-time tooling this script itself needs
# (e.g. patchelf) — never for anything the compiled `ashes` executable depends on at runtime.
ensure_command_any_package_manager() {
    local command_name="$1"
    local package_name="$2"

    if command -v "$command_name" >/dev/null 2>&1; then
        return
    fi

    echo "Installing missing prerequisite: $package_name"
    if command -v apt-get >/dev/null 2>&1; then
        as_root apt-get install -y -qq "$package_name"
    elif command -v pacman >/dev/null 2>&1; then
        as_root pacman -Sy --noconfirm --needed "$package_name"
    else
        echo "ERROR: Unsupported package manager. Install '$package_name' manually and retry." >&2
        exit 1
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

# Resolve target architecture
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

# Downloads a single .deb from $1 and extracts its data.tar.* payload into $2 (created if
# missing). Uses only `ar`/`tar`, never `dpkg-deb` — `dpkg-deb` isn't installed by default on
# non-Debian-family hosts (e.g. Arch/CachyOS), and this script must work identically on every
# supported dev environment, not just Debian-family ones.
extract_deb_payload() {
    local deb_url="$1"
    local extract_dir="$2"

    require_command ar "Install binutils and retry."
    require_command tar "Install tar and retry."

    local tmpdir
    tmpdir=$(mktemp -d)
    local deb_file="$tmpdir/package.deb"

    wget -q -O "$deb_file" "$deb_url"

    local data_member
    data_member=$(ar t "$deb_file" | grep -E '^data\.tar\.(xz|gz|zst)$' | head -1 || true)
    if [ -z "$data_member" ]; then
        echo "ERROR: Could not find a data.tar.* member in $deb_url" >&2
        rm -rf "$tmpdir"
        exit 1
    fi

    local data_tar="$tmpdir/$data_member"
    ar p "$deb_file" "$data_member" > "$data_tar"

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

    rm -rf "$tmpdir"
}

# Resolves a Debian-style package's Filename: field from a dists Packages index (plain text,
# falling back to gzip-compressed), trying each given component in order. Echoes the
# archive-relative path (e.g. "pool/universe/z/z3/libz3-4_..._amd64.deb") on success.
# $1 = base archive URL (e.g. https://apt.llvm.org/noble or https://archive.ubuntu.com/ubuntu)
# $2 = dists path segment (e.g. llvm-toolchain-noble-22, or noble)
# $3 = deb_arch (amd64 | arm64)
# $4 = package name
# $5.. = component candidates to try in order (e.g. main universe)
resolve_deb_filename() {
    local base_url="$1" dists="$2" deb_arch="$3" package="$4"
    shift 4
    local components=("$@")

    local component
    for component in "${components[@]}"; do
        local index_plain_url="${base_url}/dists/${dists}/${component}/binary-${deb_arch}/Packages"
        local index_gz_url="${index_plain_url}.gz"
        local tmpdir
        tmpdir=$(mktemp -d)
        local index_file="$tmpdir/Packages"

        if wget -q -O "$index_file" "$index_plain_url" 2>/dev/null; then
            :
        elif wget -q -O "$tmpdir/Packages.gz" "$index_gz_url" 2>/dev/null; then
            require_command gzip "Install gzip and retry."
            gzip -dc "$tmpdir/Packages.gz" > "$index_file"
        else
            rm -rf "$tmpdir"
            continue
        fi

        local filename
        filename=$(awk -v pkg="$package" '
            $1 == "Package:" { in_pkg = ($2 == pkg) }
            in_pkg && $1 == "Filename:" { print $2 }
        ' "$index_file" | head -1)
        rm -rf "$tmpdir"

        if [ -n "$filename" ]; then
            echo "$filename"
            return
        fi
    done

    echo "ERROR: Could not locate package '${package}' for ${deb_arch} under dists/${dists} (tried components: ${components[*]})." >&2
    exit 1
}

# Downloads libLLVM.so for $2 directly from apt.llvm.org's package archive (never via the
# local system's package manager, and identically whether $2 matches the host arch or not),
# so the vendored file is fully reproducible from any dev machine or CI runner and is never
# tied to whatever a particular machine's package manager happens to have installed at the
# moment the script runs.
download_linux_llvm() {
    local llvm_major="$1"
    local target_normalized="$2"
    local codename="$3"
    local rid deb_arch

    case "$target_normalized" in
        x64) rid="linux-x64"; deb_arch="amd64" ;;
        arm64) rid="linux-arm64"; deb_arch="arm64" ;;
    esac

    echo ""
    echo "=== Downloading LLVM ${llvm_major} shared library for $rid ==="

    local suite="llvm-toolchain-${codename}-${llvm_major}"
    local base_url="https://apt.llvm.org/${codename}"

    local deb_rel_path
    deb_rel_path=$(resolve_deb_filename "$base_url" "$suite" "$deb_arch" "libllvm${llvm_major}" "main")

    local extract_dir
    extract_dir=$(mktemp -d)
    echo "  Downloading ${base_url}/${deb_rel_path} ..."
    extract_deb_payload "${base_url}/${deb_rel_path}" "$extract_dir"

    local so_path
    so_path=$(find -L "$extract_dir" -type f \( -name "libLLVM-${llvm_major}.so" -o -name "libLLVM.so.${llvm_major}*" -o -name "libLLVM-${llvm_major}.so.*" \) 2>/dev/null | sort -V | tail -1 || true)
    if [ -z "$so_path" ]; then
        echo "ERROR: libLLVM for ${deb_arch} not found in extracted libllvm${llvm_major} package." >&2
        find "$extract_dir" -iname 'libLLVM*' >&2 || true
        rm -rf "$extract_dir"
        exit 1
    fi

    local linux_out="$LIB_DIR/$rid"
    mkdir -p "$linux_out"
    cp -f "$so_path" "$linux_out/libLLVM.so"
    echo "  -> $linux_out/libLLVM.so ($(du -Lh "$linux_out/libLLVM.so" | cut -f1))"

    rm -rf "$extract_dir"

    if grep -qi microsoft /proc/version 2>/dev/null; then
        echo ""
        echo "  (WSL detected — file was copied, not symlinked, so it is"
        echo "   accessible from the Windows side via the repo's runtimes/ directory.)"
    fi
}

# Downloads libLLVM.so's six non-universal shared-library dependencies (LLVM_DEPENDENCY_PACKAGES
# above) straight from Ubuntu's package archive, and sets an $ORIGIN RPATH on the vendored
# libLLVM.so so it resolves them from runtimes/<target>/ first, every time — never falling
# back to whatever (if anything) happens to be installed system-wide, on any host.
download_linux_llvm_dependencies() {
    local target_normalized="$1"
    local codename="$2"
    local rid deb_arch archive_base

    case "$target_normalized" in
        x64) rid="linux-x64"; deb_arch="amd64"; archive_base="https://archive.ubuntu.com/ubuntu" ;;
        arm64) rid="linux-arm64"; deb_arch="arm64"; archive_base="https://ports.ubuntu.com/ubuntu-ports" ;;
    esac

    echo ""
    echo "=== Downloading libLLVM's native dependencies for $rid ==="

    ensure_command_any_package_manager patchelf patchelf

    local linux_out="$LIB_DIR/$rid"
    mkdir -p "$linux_out"

    local entry package want_sos want_so deb_rel_path extract_dir found_so
    for entry in "${LLVM_DEPENDENCY_PACKAGES[@]}"; do
        package="${entry%%:*}"
        IFS=',' read -r -a want_sos <<< "${entry#*:}"

        deb_rel_path=$(resolve_deb_filename "$archive_base" "$codename" "$deb_arch" "$package" "main" "universe")

        extract_dir=$(mktemp -d)
        echo "  Downloading ${archive_base}/${deb_rel_path} ..."
        extract_deb_payload "${archive_base}/${deb_rel_path}" "$extract_dir"

        for want_so in "${want_sos[@]}"; do
            found_so=$(find -L "$extract_dir" -type f -name "$want_so" 2>/dev/null | head -1 || true)
            if [ -z "$found_so" ]; then
                echo "ERROR: ${want_so} not found in extracted ${package} package." >&2
                find "$extract_dir" -iname "lib*" >&2 || true
                rm -rf "$extract_dir"
                exit 1
            fi

            cp -f "$found_so" "$linux_out/$want_so"
            echo "  -> $linux_out/$want_so"

            # Each vendored file needs its OWN $ORIGIN RPATH, not just libLLVM.so's: the loader
            # resolves a library's transitive dependencies (e.g. libedit.so.2 -> libtinfo.so.6,
            # libxml2.so.2 -> libicuuc.so.74) using THAT library's own RPATH, never the RPATH of
            # whatever loaded it — so every link in the chain must point back at this directory.
            patchelf --set-rpath '$ORIGIN' "$linux_out/$want_so"
        done
        rm -rf "$extract_dir"
    done

    patchelf --set-rpath '$ORIGIN' "$linux_out/libLLVM.so"
    echo "  -> Set \$ORIGIN RPATH on libLLVM.so and all its vendored dependencies"
}

# Helper: download everything needed for one Linux target (libLLVM.so + its six dependencies),
# identically regardless of the host's own architecture or package manager.
download_linux() {
    local llvm_major="$1"
    local target_normalized="$2"

    local codename
    codename=$(resolve_llvm_apt_codename "$llvm_major")

    download_linux_llvm "$llvm_major" "$target_normalized" "$codename"
    download_linux_llvm_dependencies "$target_normalized" "$codename"
}

# Helper: download a Windows LLVM-C.dll from the GitHub release.
# $1 = full LLVM version, $2 = LLVM host triple arch (x86_64 | aarch64), $3 = Ashes rid (win-x64 | win-arm64).
download_windows_dll_arch() {
    local llvm_version="$1" llvm_arch="$2" rid="$3"

    echo ""
    echo "=== Downloading LLVM ${llvm_version} ${rid} DLL ==="

    ensure_command xz xz-utils

    local win_url="https://github.com/llvm/llvm-project/releases/download/llvmorg-${llvm_version}/clang+llvm-${llvm_version}-${llvm_arch}-pc-windows-msvc.tar.xz"
    local win_out="$LIB_DIR/${rid}"
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
        echo "ERROR: Could not find LLVM-C.dll in ${rid} archive" >&2
        rm -rf "$tmpdir"
        exit 1
    fi

    cp -f "$llvm_c_dll" "$win_out/libLLVM.dll"
    local size
    size=$(du -h "$win_out/libLLVM.dll" | cut -f1)
    echo "  -> $win_out/libLLVM.dll ($size)"

    rm -rf "$tmpdir"
}

# Helper: download Windows x64 DLL from GitHub release.
download_windows_dll() {
    download_windows_dll_arch "$1" "x86_64" "win-x64"
}

# Helper: download Windows ARM64 DLL from GitHub release.
download_windows_arm64_dll() {
    download_windows_dll_arch "$1" "aarch64" "win-arm64"
}

# Main
if [ "$ALL_MODE" = true ]; then
    # Download all four runtimes: linux-x64, linux-arm64, win-x64, win-arm64
    download_linux "$LLVM_MAJOR" "x64"
    download_linux "$LLVM_MAJOR" "arm64"
    download_windows_dll "$LLVM_FULL_VERSION"
    download_windows_arm64_dll "$LLVM_FULL_VERSION"

    echo ""
    echo "=== Done (LLVM ${LLVM_MAJOR}, all runtimes: linux-x64, linux-arm64, win-x64, win-arm64) ==="
elif [ "$WIN_X64_MODE" = true ]; then
    download_windows_dll "$LLVM_FULL_VERSION"

    echo ""
    echo "=== Done (LLVM ${LLVM_FULL_VERSION}, win-x64) ==="
elif [ "$WIN_ARM64_MODE" = true ]; then
    download_windows_arm64_dll "$LLVM_FULL_VERSION"

    echo ""
    echo "=== Done (LLVM ${LLVM_FULL_VERSION}, win-arm64) ==="
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
