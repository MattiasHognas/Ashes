#!/usr/bin/env bash
# download-openlibm.sh
# Provisions openlibm shared-library payloads for the Ashes.Math native layer.
#
# Unlike rustls-ffi, openlibm publishes no prebuilt release binaries, so every
# target is built from the pinned source tag.
#
# Supported outputs:
#   - runtimes/linux-x64/libopenlibm.so    (built with the host gcc)
#   - runtimes/linux-arm64/libopenlibm.so  (cross-built with aarch64-linux-gnu-gcc)
#   - runtimes/win-x64/openlibm.dll        (cross-built with x86_64-w64-mingw32-gcc)
#
# This script is intended to run on Linux directly or under WSL on Windows.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNTIMES_DIR="$REPO_ROOT/runtimes"
TARGETS=()
SUDO=()

read_openlibm_version_from_props() {
    local props_path="$REPO_ROOT/Directory.Build.props"
    local version

    if [ ! -f "$props_path" ]; then
        echo "ERROR: Missing '$props_path'." >&2
        exit 1
    fi

    version="$(sed -n 's:.*<OpenlibmVersion>\(.*\)</OpenlibmVersion>.*:\1:p' "$props_path" | head -n 1)"
    if [ -z "$version" ]; then
        echo "ERROR: Could not read <OpenlibmVersion> from '$props_path'." >&2
        exit 1
    fi

    printf '%s\n' "$version"
}

OPENLIBM_VERSION="$(read_openlibm_version_from_props)"

usage() {
    cat <<'EOF'
Usage:
  ./scripts/download-openlibm.sh
  ./scripts/download-openlibm.sh --all
  ./scripts/download-openlibm.sh --linux-x64 --win-x64
  ./scripts/download-openlibm.sh --linux-arm64
  ./scripts/download-openlibm.sh --version 0.8.7 --linux-x64

Defaults:
  - Without explicit target switches, builds the native Linux payload.

Notes:
  - openlibm publishes no prebuilt binaries; every target is built from source.
  - linux-x64 builds with the host gcc/make.
  - linux-arm64 is cross-built with aarch64-linux-gnu-gcc.
    - On apt-based and pacman-based systems, the script installs the cross-compiler
      automatically when needed; otherwise install gcc-aarch64-linux-gnu first.
  - win-x64 is cross-built with the MinGW-w64 toolchain (x86_64-w64-mingw32-gcc).
    - On apt-based and pacman-based systems, the script installs mingw-w64
      automatically when needed; otherwise install it first.
  - The default openlibm version comes from Directory.Build.props; --version overrides it.
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

ensure_root_access() {
    if [ "$(id -u)" -eq 0 ]; then
        SUDO=()
        return
    fi

    if command -v sudo >/dev/null 2>&1; then
        SUDO=(sudo)
        return
    fi

    echo "ERROR: Missing root access or sudo to install cross-build prerequisites automatically." >&2
    echo "Install the required cross toolchain manually and retry." >&2
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

resolve_aarch64_gnu_compiler() {
    if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
        printf '%s\n' "aarch64-linux-gnu-"
        return
    fi

    if [ "$(detect_package_manager)" = "apt" ]; then
        ensure_root_access
        echo "Installing missing prerequisite: gcc-aarch64-linux-gnu" >&2
        as_root apt-get update -qq
        as_root apt-get install -y -qq gcc-aarch64-linux-gnu
        if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
            printf '%s\n' "aarch64-linux-gnu-"
            return
        fi
    fi

    if [ "$(detect_package_manager)" = "pacman" ]; then
        ensure_root_access
        echo "Installing missing prerequisite: aarch64-linux-gnu-gcc" >&2
        as_root pacman -Sy --noconfirm --needed aarch64-linux-gnu-gcc
        if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
            printf '%s\n' "aarch64-linux-gnu-"
            return
        fi
    fi

    echo "ERROR: linux-arm64 build requires an aarch64 GNU cross-compiler." >&2
    echo "Install 'gcc-aarch64-linux-gnu' (apt) or 'aarch64-linux-gnu-gcc' (pacman) and retry." >&2
    exit 1
}

resolve_mingw_compiler() {
    if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
        printf '%s\n' "x86_64-w64-mingw32-"
        return
    fi

    if [ "$(detect_package_manager)" = "apt" ]; then
        ensure_root_access
        echo "Installing missing prerequisite: mingw-w64" >&2
        as_root apt-get update -qq
        as_root apt-get install -y -qq mingw-w64
        if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
            printf '%s\n' "x86_64-w64-mingw32-"
            return
        fi
    fi

    if [ "$(detect_package_manager)" = "pacman" ]; then
        ensure_root_access
        echo "Installing missing prerequisite: mingw-w64-gcc" >&2
        as_root pacman -Sy --noconfirm --needed mingw-w64-gcc
        if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
            printf '%s\n' "x86_64-w64-mingw32-"
            return
        fi
    fi

    echo "ERROR: win-x64 build requires the MinGW-w64 toolchain (x86_64-w64-mingw32-gcc)." >&2
    echo "Install 'mingw-w64' (apt) or 'mingw-w64-gcc' (pacman) and retry." >&2
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
            OPENLIBM_VERSION="$2"
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
require_command tar "Install tar and retry."
require_command make "Install make and retry."

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

for target in "${TARGETS[@]}"; do
    ensure_directory_writable "$RUNTIMES_DIR/$target"
done

write_version_marker() {
    local target_id="$1"
    local output_path="$RUNTIMES_DIR/$target_id/openlibm.version"
    local tmp_path

    tmp_path="$(mktemp "$RUNTIMES_DIR/$target_id/.openlibm.version.XXXXXX")"
    printf '%s\n' "$OPENLIBM_VERSION" > "$tmp_path"
    chmod 0644 "$tmp_path"
    mv -f "$tmp_path" "$output_path"
}

# Fetch the pinned openlibm source into a fresh temp dir and echo the extracted path.
fetch_openlibm_source() {
    local tmpdir="$1"
    local url="https://github.com/JuliaMath/openlibm/archive/refs/tags/v${OPENLIBM_VERSION}.tar.gz"

    curl -fsSL "$url" | tar -xz -C "$tmpdir"
    printf '%s\n' "$tmpdir/openlibm-${OPENLIBM_VERSION}"
}

build_linux_x64() {
    local output_path="$RUNTIMES_DIR/linux-x64/libopenlibm.so"
    local tmpdir src
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    require_command gcc "Install gcc and retry."

    echo "=== Building openlibm linux-x64 ${OPENLIBM_VERSION} ==="
    src="$(fetch_openlibm_source "$tmpdir")"
    make -C "$src" -j USE_GCC=1 >/dev/null
    cp -f "$src"/libopenlibm.so.*.* "$output_path"
    write_version_marker "linux-x64"
    echo "  -> $output_path"

    rm -rf "$tmpdir"
    trap - RETURN
}

build_linux_arm64() {
    local output_path="$RUNTIMES_DIR/linux-arm64/libopenlibm.so"
    local tmpdir src toolprefix
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    if [ "$HOST_ARCH" = "arm64" ]; then
        require_command gcc "Install gcc and retry."
        echo "=== Building openlibm linux-arm64 ${OPENLIBM_VERSION} natively ==="
        src="$(fetch_openlibm_source "$tmpdir")"
        make -C "$src" -j USE_GCC=1 >/dev/null
    else
        toolprefix="$(resolve_aarch64_gnu_compiler)"
        echo "=== Building openlibm linux-arm64 ${OPENLIBM_VERSION} via cross-compilation ==="
        src="$(fetch_openlibm_source "$tmpdir")"
        make -C "$src" -j USE_GCC=1 ARCH=aarch64 TOOLPREFIX="$toolprefix" >/dev/null
    fi

    cp -f "$src"/libopenlibm.so.*.* "$output_path"
    write_version_marker "linux-arm64"
    echo "  -> $output_path"

    rm -rf "$tmpdir"
    trap - RETURN
}

build_win_x64() {
    local output_path="$RUNTIMES_DIR/win-x64/openlibm.dll"
    local tmpdir src toolprefix
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    toolprefix="$(resolve_mingw_compiler)"

    echo "=== Building openlibm win-x64 ${OPENLIBM_VERSION} via cross-compilation ==="
    src="$(fetch_openlibm_source "$tmpdir")"
    make -C "$src" -j USE_GCC=1 OS=WINNT ARCH=x86_64 TOOLPREFIX="$toolprefix" >/dev/null

    if [ -f "$src/libopenlibm.dll" ]; then
        cp -f "$src/libopenlibm.dll" "$output_path"
    elif [ -f "$src/openlibm.dll" ]; then
        cp -f "$src/openlibm.dll" "$output_path"
    else
        echo "ERROR: openlibm win-x64 build did not produce a .dll under '$src'." >&2
        exit 1
    fi
    write_version_marker "win-x64"
    echo "  -> $output_path"

    rm -rf "$tmpdir"
    trap - RETURN
}

for target in "${TARGETS[@]}"; do
    case "$target" in
        linux-x64)
            build_linux_x64
            ;;
        linux-arm64)
            build_linux_arm64
            ;;
        win-x64)
            build_win_x64
            ;;
        *)
            echo "ERROR: Unsupported target '$target'." >&2
            exit 1
            ;;
    esac
done

echo
echo "openlibm payloads are available under:"
for target in "${TARGETS[@]}"; do
    case "$target" in
        linux-x64)
            echo "  $RUNTIMES_DIR/linux-x64/libopenlibm.so"
            ;;
        linux-arm64)
            echo "  $RUNTIMES_DIR/linux-arm64/libopenlibm.so"
            ;;
        win-x64)
            echo "  $RUNTIMES_DIR/win-x64/openlibm.dll"
            ;;
    esac
done
