#!/usr/bin/env bash
# download-openlibm.sh
# Provisions the openlibm LLVM-bitcode payload for the Ashes.Math native layer.
#
# openlibm publishes no prebuilt release binaries, so the payload is compiled from the
# pinned source tag into a minimal, self-contained bitcode module (see build_openlibm_bitcode).
#
# Supported outputs:
#   - runtimes/linux-x64/libopenlibm.bc
#   - runtimes/linux-arm64/libopenlibm.bc
#   - runtimes/win-x64/libopenlibm.bc
#
# This script is intended to run on Linux directly or under WSL on Windows.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNTIMES_DIR="$REPO_ROOT/runtimes"
TARGETS=()

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
  - Without explicit target switches, builds the payload for the host Linux arch.

Notes:
  - openlibm publishes no prebuilt binaries; the payload is compiled from the pinned source.
  - The payload is LLVM bitcode (libopenlibm.bc), which the compiler links into programs that use
    Ashes.Math transcendentals. Bitcode is produced by the clang frontend, so every target's payload
    is built on this host with clang alone -- no cross toolchain is required for linux-arm64/win-x64.
  - Requires: curl, tar, make, ar, clang, llvm-link, opt (make/ar/host cc are used once to enumerate
    openlibm's curated source set from its .a).
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
require_command ar "Install binutils (ar) and retry."
require_command clang "Install clang and retry."
require_command llvm-link "Install the LLVM tools (llvm-link) and retry."
require_command opt "Install the LLVM tools (opt) and retry."

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

openlibm_triple_for() {
    case "$1" in
        linux-x64) echo "x86_64-unknown-linux-gnu" ;;
        linux-arm64) echo "aarch64-unknown-linux-gnu" ;;
        win-x64) echo "x86_64-pc-windows-msvc" ;;
        *) echo "ERROR: Unsupported target '$1'." >&2; exit 1 ;;
    esac
}

# Builds the vendored openlibm LLVM bitcode for one target. Because LLVM bitcode is produced by the
# clang frontend, every target's payload is built on this host with clang alone (no cross toolchain).
# The result is a minimal, self-contained module: only the double-precision transcendentals and the
# libm functions the backend's instruction selection may lower to (round/floor/... exp2/log2), plus
# the float classifiers and no-op fenv shims. Nothing dynamically links against a system libm.
build_openlibm_bitcode() {
    local rid="$1"
    local triple output_path tmpdir src bc a members base s
    triple="$(openlibm_triple_for "$rid")"
    output_path="$RUNTIMES_DIR/$rid/libopenlibm.bc"
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    echo "=== Building openlibm bitcode ${rid} (${OPENLIBM_VERSION}) ==="
    src="$(fetch_openlibm_source "$tmpdir")"

    # openlibm's own Makefile selects a conflict-free object set; build it once (host) to enumerate
    # the curated source list, then compile those portable C sources to bitcode for the target triple.
    make -C "$src" -j USE_GCC=1 >/dev/null 2>&1

    bc="$tmpdir/bc"; mkdir -p "$bc"
    local cflags="--target=${triple} -emit-llvm -O2 -fno-stack-protector -ffreestanding -fno-builtin -DNDEBUG -I ${src}/include -I ${src}/src"
    while IFS= read -r m; do
        case "$m" in *.o) ;; *) continue;; esac
        base="${m%.o}"; base="${base%.c}"; base="${base%.S}"
        s="${src}/src/${base}.c"
        [ -f "$s" ] && clang $cflags -c "$s" -o "$bc/$base.bc" 2>/dev/null || true
    done < <(ar t "$src/libopenlibm.a" | sort -u)

    # s_isinf/s_isnan define the float classifiers openlibm references but its .a omits; the fenv
    # shims satisfy the FP-environment calls in a freestanding image.
    clang $cflags -c "$src/src/s_isinf.c" -o "$bc/s_isinf.bc" 2>/dev/null || true
    clang $cflags -c "$src/src/s_isnan.c" -o "$bc/s_isnan.bc" 2>/dev/null || true
    printf '%s\n' \
        'int feholdexcept(void*e){(void)e;return 0;}' \
        'int feupdateenv(const void*e){(void)e;return 0;}' \
        'int fegetenv(void*e){(void)e;return 0;}' \
        'int fesetenv(const void*e){(void)e;return 0;}' \
        'int feraiseexcept(int e){(void)e;return 0;}' \
        'int feclearexcept(int e){(void)e;return 0;}' \
        'int fetestexcept(int e){(void)e;return 0;}' \
        'int fesetround(int r){(void)r;return 0;}' \
        'int fegetround(void){return 0;}' > "$tmpdir/fenv_stubs.c"
    clang $cflags -c "$tmpdir/fenv_stubs.c" -o "$bc/fenv_stubs.bc"

    llvm-link "$bc"/*.bc -o "$tmpdir/all.bc"

    local keep="sin,cos,tan,asin,acos,atan,atan2,sinh,cosh,tanh,exp,expm1,log,log2,log10,log1p,pow,cbrt,hypot,fmod"
    keep="$keep,floor,ceil,trunc,round,rint,nearbyint,sqrt,fma,copysign,fabs,fmax,fmin,scalbn,ldexp,exp2"
    keep="$keep,__isinf,__isnan,__isinff,__isnanf"
    keep="$keep,feholdexcept,feupdateenv,fegetenv,fesetenv,feraiseexcept,feclearexcept,fetestexcept,fesetround,fegetround"
    opt -passes='internalize,globaldce' -internalize-public-api-list="$keep" "$tmpdir/all.bc" -o "$output_path"

    write_version_marker "$rid"
    echo "  -> $output_path ($(stat -c%s "$output_path") bytes)"

    rm -rf "$tmpdir"
    trap - RETURN
}

for target in "${TARGETS[@]}"; do
    build_openlibm_bitcode "$target"
done

echo
echo "openlibm payloads are available under:"
for target in "${TARGETS[@]}"; do
    case "$target" in
        linux-x64)
            echo "  $RUNTIMES_DIR/linux-x64/libopenlibm.bc"
            ;;
        linux-arm64)
            echo "  $RUNTIMES_DIR/linux-arm64/libopenlibm.bc"
            ;;
        win-x64)
            echo "  $RUNTIMES_DIR/win-x64/libopenlibm.bc"
            ;;
    esac
done
