#!/usr/bin/env bash
# download-rustls-ffi.sh
# Provisions rustls-ffi shared-library payloads for Ashes hermetic TLS work.
#
# Supported outputs:
#   - runtimes/linux-x64/librustls.so    (downloaded from upstream release zip)
#   - runtimes/win-x64/rustls.dll        (downloaded from upstream release zip)
#   - runtimes/linux-arm64/librustls.so  (native or cross-built from source)
#
# This script is intended to run on Linux directly or under WSL on Windows.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNTIMES_DIR="$REPO_ROOT/runtimes"
TARGETS=()
SUDO=()

read_rustls_version_from_props() {
    local props_path="$REPO_ROOT/Directory.Build.props"
    local version

    if [ ! -f "$props_path" ]; then
        echo "ERROR: Missing '$props_path'." >&2
        exit 1
    fi

    version="$(sed -n 's:.*<RustlsFfiVersion>\(.*\)</RustlsFfiVersion>.*:\1:p' "$props_path" | head -n 1)"
    if [ -z "$version" ]; then
        echo "ERROR: Could not read <RustlsFfiVersion> from '$props_path'." >&2
        exit 1
    fi

    printf '%s\n' "$version"
}

RUSTLS_VERSION="$(read_rustls_version_from_props)"

usage() {
    cat <<'EOF'
Usage:
  ./scripts/download-rustls-ffi.sh
  ./scripts/download-rustls-ffi.sh --all
  ./scripts/download-rustls-ffi.sh --linux-x64 --win-x64
  ./scripts/download-rustls-ffi.sh --linux-arm64
  ./scripts/download-rustls-ffi.sh --version 0.15.3 --linux-x64

Defaults:
  - Without explicit target switches, downloads the native Linux payload.

Notes:
  - Windows payload download works from Linux/WSL and stages rustls.dll under runtimes/win-x64/.
  - linux-arm64 is source-built because upstream does not publish a prebuilt Linux arm64 shared library.
    - Cross-building linux-arm64 from x64 requires cargo, rustup, and an aarch64 GNU linker.
    - On apt-based and pacman-based systems, the script installs the cross-linker automatically when needed.
    - On other systems, install aarch64-linux-gnu-gcc (or aarch64-unknown-linux-gnu-gcc) before running.
    - The default rustls-ffi version comes from Directory.Build.props; --version overrides it.
EOF
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

ensure_cargo_c() {
    if cargo capi --version >/dev/null 2>&1; then
        return
    fi

    echo "Installing missing prerequisite: cargo-c"
    cargo install cargo-c --locked
}

ensure_root_access() {
    if [ "$(id -u)" -eq 0 ]; then
        SUDO=()
        return
    fi

    if command -v sudo >/dev/null 2>&1; then
        SUDO=(sudo)
        return
    fi

    echo "ERROR: Missing root access or sudo to install linux-arm64 cross-build prerequisites automatically." >&2
    echo "Install 'gcc-aarch64-linux-gnu' manually and retry." >&2
    exit 1
}

as_root() {
    if [ "${#SUDO[@]}" -eq 0 ]; then
        "$@"
    else
        "${SUDO[@]}" "$@"
    fi
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

    echo "unknown"
}

resolve_aarch64_gnu_linker() {
    if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
        printf '%s\n' "aarch64-linux-gnu-gcc"
        return
    fi

    if command -v aarch64-unknown-linux-gnu-gcc >/dev/null 2>&1; then
        printf '%s\n' "aarch64-unknown-linux-gnu-gcc"
        return
    fi

    if [ "$(detect_package_manager)" = "apt" ]; then
        ensure_root_access
        echo "Installing missing prerequisite: gcc-aarch64-linux-gnu"
        as_root apt-get update -qq
        as_root apt-get install -y -qq gcc-aarch64-linux-gnu

        if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
            printf '%s\n' "aarch64-linux-gnu-gcc"
            return
        fi
    fi

    if [ "$(detect_package_manager)" = "pacman" ]; then
        ensure_root_access
        echo "Installing missing prerequisite: aarch64-linux-gnu-gcc"
        as_root pacman -Sy --noconfirm --needed aarch64-linux-gnu-gcc

        if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
            printf '%s\n' "aarch64-linux-gnu-gcc"
            return
        fi
    fi

    echo "ERROR: linux-arm64 cross-build requires an aarch64 GNU linker." >&2
    echo "Install 'gcc-aarch64-linux-gnu' (apt) or provide 'aarch64-linux-gnu-gcc' / 'aarch64-unknown-linux-gnu-gcc' on PATH." >&2
    exit 1
}

normalize_arch() {
    case "$1" in
        x86_64|amd64|x64)
            echo "x64"
            ;;
        aarch64|arm64)
            echo "arm64"
            ;;
        *)
            echo "ERROR: Unsupported architecture '$1'." >&2
            exit 1
            ;;
    esac
}

ensure_target_selected() {
    local target="$1"
    for existing in "${TARGETS[@]:-}"; do
        if [ "$existing" = "$target" ]; then
            return
        fi
    done

    TARGETS+=("$target")
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --all)
            ensure_target_selected "linux-x64"
            ensure_target_selected "linux-arm64"
            ensure_target_selected "win-x64"
            shift
            ;;
        --linux-x64)
            ensure_target_selected "linux-x64"
            shift
            ;;
        --linux-arm64)
            ensure_target_selected "linux-arm64"
            shift
            ;;
        --win-x64)
            ensure_target_selected "win-x64"
            shift
            ;;
        --version)
            if [ "$#" -lt 2 ]; then
                echo "ERROR: --version requires a value." >&2
                exit 1
            fi
            RUSTLS_VERSION="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "ERROR: Unknown argument '$1'." >&2
            usage >&2
            exit 1
            ;;
    esac
done

HOST_ARCH="$(normalize_arch "$(uname -m)")"
if [ "${#TARGETS[@]}" -eq 0 ]; then
    TARGETS=("linux-$HOST_ARCH")
fi

require_command curl "Install curl and retry."
require_command unzip "Install unzip and retry."
require_command tar "Install tar and retry."

ensure_directory_writable() {
    local dir_path="$1"

    if [ ! -d "$dir_path" ]; then
        if ! mkdir -p "$dir_path"; then
            echo "ERROR: Could not create '$dir_path'." >&2
            exit 1
        fi
    fi

    if [ ! -w "$dir_path" ]; then
        echo "ERROR: Cannot write to '$dir_path'." >&2
        ls -ld "$dir_path" >&2 || true
        echo "Fix ownership or permissions and retry, for example:" >&2
        echo "  sudo chown -R $(id -un):$(id -gn) '$dir_path'" >&2
        exit 1
    fi
}

ensure_target_writable() {
    case "$1" in
        linux-x64)
            ensure_directory_writable "$RUNTIMES_DIR/linux-x64"
            ;;
        linux-arm64)
            ensure_directory_writable "$RUNTIMES_DIR/linux-arm64"
            ;;
        win-x64)
            ensure_directory_writable "$RUNTIMES_DIR/win-x64"
            ;;
        *)
            echo "ERROR: Unsupported target '$1'." >&2
            exit 1
            ;;
    esac
}

for target in "${TARGETS[@]}"; do
    ensure_target_writable "$target"
done

download_release_entry() {
    local url="$1"
    local archive_name="$2"
    local entry_path="$3"
    local output_path="$4"

    local tmpdir
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    pushd "$tmpdir" >/dev/null
    curl -fsSLo "$archive_name" "$url"
    unzip -q "$archive_name"

    if [ ! -f "$entry_path" ]; then
        echo "ERROR: Expected '$entry_path' in '$archive_name'." >&2
        exit 1
    fi

    cp -f "$entry_path" "$output_path"
    popd >/dev/null
    rm -rf "$tmpdir"
    trap - RETURN
}

write_version_marker() {
    local target_id="$1"
    local output_path="$RUNTIMES_DIR/$target_id/rustls.version"
    local tmp_path

    tmp_path="$(mktemp "$RUNTIMES_DIR/$target_id/.rustls.version.XXXXXX")"
    printf '%s\n' "$RUSTLS_VERSION" > "$tmp_path"
    mv -f "$tmp_path" "$output_path"
}

download_linux_x64() {
    local output_path="$RUNTIMES_DIR/linux-x64/librustls.so"
    local url="https://github.com/rustls/rustls-ffi/releases/download/v${RUSTLS_VERSION}/rustls-ffi-x86_64-linux-gnu.zip"

    echo "=== Downloading rustls-ffi linux-x64 ${RUSTLS_VERSION} ==="
    download_release_entry "$url" "rustls-ffi-x86_64-linux-gnu.zip" "lib/librustls.so" "$output_path"
    write_version_marker "linux-x64"
    echo "  -> $output_path"
}

download_windows_x64() {
    local output_path="$RUNTIMES_DIR/win-x64/rustls.dll"
    local url="https://github.com/rustls/rustls-ffi/releases/download/v${RUSTLS_VERSION}/rustls-ffi-x86_64-windows.zip"

    echo "=== Downloading rustls-ffi win-x64 ${RUSTLS_VERSION} ==="
    download_release_entry "$url" "rustls-ffi-x86_64-windows.zip" "bin/rustls.dll" "$output_path"
    write_version_marker "win-x64"
    echo "  -> $output_path"
}

build_linux_arm64() {
    local output_path="$RUNTIMES_DIR/linux-arm64/librustls.so"
    local tmpdir
    local linker=""
    local prefix_dir=""
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    require_command cargo "Install Rust and cargo to build linux-arm64 rustls-ffi."
    ensure_cargo_c

    if [ "$HOST_ARCH" = "arm64" ]; then
        echo "=== Building rustls-ffi linux-arm64 ${RUSTLS_VERSION} natively ==="
        pushd "$tmpdir" >/dev/null
        curl -fsSL "https://github.com/rustls/rustls-ffi/archive/refs/tags/v${RUSTLS_VERSION}.tar.gz" | tar -xz
        pushd "rustls-ffi-${RUSTLS_VERSION}" >/dev/null
        prefix_dir="$tmpdir/stage"
        cargo capi install --release --locked --prefix "$prefix_dir" --libdir lib
        cp -f "$prefix_dir/lib/librustls.so" "$output_path"
        write_version_marker "linux-arm64"
        popd >/dev/null
        popd >/dev/null
        rm -rf "$tmpdir"
        trap - RETURN
        echo "  -> $output_path"
        return
    fi

    require_command rustup "Install rustup so the aarch64-unknown-linux-gnu target can be added."

    linker="$(resolve_aarch64_gnu_linker)"

    echo "=== Building rustls-ffi linux-arm64 ${RUSTLS_VERSION} via cross-compilation ==="
    pushd "$tmpdir" >/dev/null
    curl -fsSL "https://github.com/rustls/rustls-ffi/archive/refs/tags/v${RUSTLS_VERSION}.tar.gz" | tar -xz
    pushd "rustls-ffi-${RUSTLS_VERSION}" >/dev/null
    rustup target add aarch64-unknown-linux-gnu >/dev/null
    prefix_dir="$tmpdir/stage"
    CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER="$linker" cargo capi install --release --locked --target aarch64-unknown-linux-gnu --prefix "$prefix_dir" --libdir lib
    cp -f "$prefix_dir/lib/librustls.so" "$output_path"
    write_version_marker "linux-arm64"
    popd >/dev/null
    popd >/dev/null
    rm -rf "$tmpdir"
    trap - RETURN
    echo "  -> $output_path"
}

for target in "${TARGETS[@]}"; do
    case "$target" in
        linux-x64)
            download_linux_x64
            ;;
        linux-arm64)
            build_linux_arm64
            ;;
        win-x64)
            download_windows_x64
            ;;
        *)
            echo "ERROR: Unsupported target '$target'." >&2
            exit 1
            ;;
    esac
done

echo
echo "Rustls payloads are available under:"
for target in "${TARGETS[@]}"; do
    case "$target" in
        linux-x64)
            echo "  $RUNTIMES_DIR/linux-x64/librustls.so"
            ;;
        linux-arm64)
            echo "  $RUNTIMES_DIR/linux-arm64/librustls.so"
            ;;
        win-x64)
            echo "  $RUNTIMES_DIR/win-x64/rustls.dll"
            ;;
    esac
done