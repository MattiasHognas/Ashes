#!/usr/bin/env bash
# download-pcre2.sh
# Provisions the PCRE2 LLVM-bitcode payload for the Ashes.Regex native layer.
#
# PCRE2 publishes source-only release tarballs, so the payload is compiled from the pinned tag into a
# minimal, self-contained bitcode module: the 8-bit code-unit library, Unicode property support on,
# JIT off, with everything unreachable from the exposed API stripped by internalize + globaldce.
#
# The build is fully self-contained -- it needs clang, llvm-link, opt, and llvm-nm and nothing else:
#   * Because LLVM bitcode is produced by the clang frontend, every target's payload is built on this
#     one host with no cross toolchain (the target ABI/datalayout come from --target alone).
#   * The Windows payload is compiled with the windows-gnu triple (PCRE2 passes >4 integer/pointer
#     args, so it needs the Microsoft x64 calling convention, unlike openlibm's 1-2 double-arg math).
#     Since no MinGW sysroot is assumed present, a minimal set of declaration-only stub headers is
#     written to a temp dir and used only to parse the sources (see write_win_stub_headers).
#   * The handful of libc leaf functions PCRE2 references (memcmp, memchr, strlen, strchr, and the
#     ctype classifiers) are compiled into the bundle as freestanding ASCII implementations, so every
#     target's external dependency set collapses to exactly { malloc, free, memcpy, memset }. The
#     backend resolves memcpy/memset (emitted into every module) and routes malloc/free to the arena.
#
# Supported outputs:
#   - runtimes/linux-x64/libpcre2.bc
#   - runtimes/linux-arm64/libpcre2.bc
#   - runtimes/win-x64/libpcre2.bc
#
# This script is intended to run on Linux directly or under WSL on Windows.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNTIMES_DIR="$REPO_ROOT/runtimes"
TARGETS=()

read_pcre2_version_from_props() {
    local props_path="$REPO_ROOT/Directory.Build.props"
    local version

    if [ ! -f "$props_path" ]; then
        echo "ERROR: Missing '$props_path'." >&2
        exit 1
    fi

    version="$(sed -n 's:.*<Pcre2Version>\(.*\)</Pcre2Version>.*:\1:p' "$props_path" | head -n 1)"
    if [ -z "$version" ]; then
        echo "ERROR: Could not read <Pcre2Version> from '$props_path'." >&2
        exit 1
    fi

    printf '%s\n' "$version"
}

PCRE2_VERSION="$(read_pcre2_version_from_props)"

usage() {
    cat <<'EOF'
Usage:
  ./scripts/download-pcre2.sh
  ./scripts/download-pcre2.sh --all
  ./scripts/download-pcre2.sh --linux-x64 --win-x64
  ./scripts/download-pcre2.sh --linux-arm64
  ./scripts/download-pcre2.sh --version 10.45 --linux-x64

Defaults:
  - Without explicit target switches, builds the payload for the host Linux arch.

Notes:
  - PCRE2 publishes source-only releases; the payload is compiled from the pinned source to LLVM
    bitcode (libpcre2.bc), which the compiler links into programs that use Ashes.Regex.
  - Requires: curl, tar, clang, llvm-link, opt, llvm-nm. No MinGW / cross toolchain is required.
  - The default PCRE2 version comes from Directory.Build.props; --version overrides it.
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
            PCRE2_VERSION="$2"
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
require_command clang "Install clang and retry."
require_command llvm-link "Install the LLVM tools (llvm-link) and retry."
require_command opt "Install the LLVM tools (opt) and retry."
require_command llvm-nm "Install the LLVM tools (llvm-nm) and retry."

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
    local output_path="$RUNTIMES_DIR/$target_id/pcre2.version"
    local tmp_path

    tmp_path="$(mktemp "$RUNTIMES_DIR/$target_id/.pcre2.version.XXXXXX")"
    printf '%s\n' "$PCRE2_VERSION" > "$tmp_path"
    chmod 0644 "$tmp_path"
    mv -f "$tmp_path" "$output_path"
}

# Fetch the pinned PCRE2 source into a fresh temp dir and echo the extracted path.
fetch_pcre2_source() {
    local tmpdir="$1"
    local url="https://github.com/PCRE2Project/pcre2/releases/download/pcre2-${PCRE2_VERSION}/pcre2-${PCRE2_VERSION}.tar.gz"

    curl -fsSL "$url" | tar -xz -C "$tmpdir"
    printf '%s\n' "$tmpdir/pcre2-${PCRE2_VERSION}"
}

pcre2_triple_for() {
    case "$1" in
        linux-x64) echo "x86_64-unknown-linux-gnu" ;;
        linux-arm64) echo "aarch64-unknown-linux-gnu" ;;
        # PCRE2's exposed API passes more than four integer/pointer arguments (e.g. pcre2_match,
        # pcre2_substitute), so the payload must use the Microsoft x64 calling convention to match the
        # backend's windows-msvc program module. The windows-gnu triple yields the same datalayout as
        # windows-msvc (LinkModules2 gates on datalayout, not the triple string).
        win-x64) echo "x86_64-w64-windows-gnu" ;;
        *) echo "ERROR: Unsupported target '$1'." >&2; exit 1 ;;
    esac
}

# The 8-bit PCRE2 library source set (JIT, tools, fuzz/debug harnesses, and #included-only translation
# units excluded). pcre2_chartables is the shipped default table (.dist), not the generated variant.
PCRE2_SOURCES="pcre2_auto_possess pcre2_chartables pcre2_chkdint pcre2_compile pcre2_compile_class \
pcre2_config pcre2_context pcre2_convert pcre2_dfa_match pcre2_error pcre2_extuni pcre2_find_bracket \
pcre2_maketables pcre2_match pcre2_match_data pcre2_newline pcre2_ord2utf pcre2_pattern_info \
pcre2_script_run pcre2_serialize pcre2_string_utils pcre2_study pcre2_substitute pcre2_substring \
pcre2_tables pcre2_ucd pcre2_valid_utf pcre2_xclass"

# The exposed API surface: everything reachable from these roots survives globaldce; the rest is
# stripped. These are the symbols the backend's regex intrinsics call (all suffixed _8 for the 8-bit
# code-unit library).
PCRE2_KEEP="pcre2_compile_8,pcre2_match_8,pcre2_match_data_create_from_pattern_8,\
pcre2_get_ovector_pointer_8,pcre2_get_ovector_count_8,pcre2_substitute_8,\
pcre2_general_context_create_8,pcre2_get_error_message_8"

# Declaration-only stub headers, used only to parse the sources under the windows-gnu triple where no
# MinGW sysroot is assumed. clang provides limits.h/stddef.h/stdarg.h/stdint.h itself (freestanding);
# only the libc headers PCRE2 includes are stubbed. None of these functions are actually linked --
# they are satisfied by the in-bundle shims (ashes_pcre2_shims.c) or resolve to malloc/free/memcpy/
# memset externally.
write_win_stub_headers() {
    local dir="$1"
    mkdir -p "$dir"
    cat > "$dir/string.h" <<'EOF'
#ifndef _ASHES_STUB_STRING_H
#define _ASHES_STUB_STRING_H
#include <stddef.h>
void *memcpy(void *, const void *, size_t);
void *memmove(void *, const void *, size_t);
void *memset(void *, int, size_t);
int memcmp(const void *, const void *, size_t);
void *memchr(const void *, int, size_t);
size_t strlen(const char *);
char *strchr(const char *, int);
int strcmp(const char *, const char *);
int strncmp(const char *, const char *, size_t);
#endif
EOF
    cat > "$dir/stdlib.h" <<'EOF'
#ifndef _ASHES_STUB_STDLIB_H
#define _ASHES_STUB_STDLIB_H
#include <stddef.h>
void *malloc(size_t);
void free(void *);
void exit(int);
#endif
EOF
    cat > "$dir/ctype.h" <<'EOF'
#ifndef _ASHES_STUB_CTYPE_H
#define _ASHES_STUB_CTYPE_H
int isalnum(int); int isalpha(int); int isblank(int); int iscntrl(int);
int isdigit(int); int isgraph(int); int islower(int); int isprint(int);
int ispunct(int); int isspace(int); int isupper(int); int isxdigit(int);
int tolower(int); int toupper(int);
#endif
EOF
    cat > "$dir/stdio.h" <<'EOF'
#ifndef _ASHES_STUB_STDIO_H
#define _ASHES_STUB_STDIO_H
typedef struct _ASHES_FILE FILE;
extern FILE *stderr;
int fprintf(FILE *, const char *, ...);
#endif
EOF
    cat > "$dir/inttypes.h" <<'EOF'
#ifndef _ASHES_STUB_INTTYPES_H
#define _ASHES_STUB_INTTYPES_H
#include <stdint.h>
#endif
EOF
}

# Freestanding ASCII implementations of the libc leaf functions PCRE2 references on Windows that the
# Ashes backend does NOT already provide. The backend emits memcpy/memset/memcmp/bcmp/strlen into
# every program module, and Linux resolves memchr via a libc import (and glibc inlines strchr/ctype
# at bitcode-compile time), so this shim is Windows-only and deliberately omits those. It supplies
# memchr, strchr, and the ctype classifiers, which back PCRE2's default C-locale character tables
# (pcre2_maketables) and internal scans; Unicode matching uses PCRE2's own UCD tables, not these.
write_shims_c() {
    cat > "$1" <<'EOF'
typedef __SIZE_TYPE__ size_t;
void *memchr(const void *s, int c, size_t n) { const unsigned char *p = s; for (size_t i = 0; i < n; i++) { if (p[i] == (unsigned char)c) return (void *)(p + i); } return 0; }
char *strchr(const char *s, int c) { for (;; s++) { if (*s == (char)c) return (char *)s; if (!*s) return 0; } }
static int ashes_in(int c, int lo, int hi) { return c >= lo && c <= hi; }
int isdigit(int c) { return ashes_in(c, '0', '9'); }
int isupper(int c) { return ashes_in(c, 'A', 'Z'); }
int islower(int c) { return ashes_in(c, 'a', 'z'); }
int isalpha(int c) { return isupper(c) || islower(c); }
int isalnum(int c) { return isalpha(c) || isdigit(c); }
int isspace(int c) { return c == ' ' || ashes_in(c, '\t', '\r'); }
int isblank(int c) { return c == ' ' || c == '\t'; }
int iscntrl(int c) { return ashes_in(c, 0, 31) || c == 127; }
int isxdigit(int c) { return isdigit(c) || ashes_in(c, 'a', 'f') || ashes_in(c, 'A', 'F'); }
int isgraph(int c) { return ashes_in(c, '!', '~'); }
int isprint(int c) { return ashes_in(c, ' ', '~'); }
int ispunct(int c) { return isgraph(c) && !isalnum(c); }
int tolower(int c) { return isupper(c) ? c + 32 : c; }
int toupper(int c) { return islower(c) ? c - 32 : c; }
EOF
}

# Builds the vendored PCRE2 LLVM bitcode for one target. Because LLVM bitcode is produced by the clang
# frontend, every target's payload is built on this host with clang alone (no cross toolchain). The
# result is a minimal, self-contained module: the 8-bit library with Unicode support, dead-stripped to
# the code reachable from the exposed API, with only { malloc, free, memcpy, memset } left external.
build_pcre2_bitcode() {
    local rid="$1"
    local triple output_path tmpdir src bc extra_include s fail
    triple="$(pcre2_triple_for "$rid")"
    output_path="$RUNTIMES_DIR/$rid/libpcre2.bc"
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' RETURN

    echo "=== Building PCRE2 bitcode ${rid} (${PCRE2_VERSION}) ==="
    src="$(fetch_pcre2_source "$tmpdir")"

    # Prepare the pre-generated headers and default character tables shipped for building without
    # autotools/cmake, and turn on Unicode property support.
    cp "$src/src/config.h.generic" "$src/src/config.h"
    cp "$src/src/pcre2.h.generic" "$src/src/pcre2.h"
    cp "$src/src/pcre2_chartables.c.dist" "$src/src/pcre2_chartables.c"
    sed -i 's:/\* #undef SUPPORT_UNICODE \*/:#define SUPPORT_UNICODE 1:' "$src/src/config.h"

    # Windows needs stub headers to parse (no MinGW sysroot assumed) plus the memchr/strchr/ctype
    # shim, because glibc inlines those on Linux but the Windows toolchain leaves them external. The
    # Linux payloads need neither: glibc inlines strchr/ctype at compile time, memchr resolves via the
    # backend's libc import, and the remaining leaf functions are backend-emitted.
    extra_include=""
    local expected_undefined
    if [ "$rid" = "win-x64" ]; then
        write_win_stub_headers "$tmpdir/winstub"
        extra_include="-isystem $tmpdir/winstub"
        write_shims_c "$tmpdir/ashes_pcre2_shims.c"
        expected_undefined="free malloc memcmp memcpy memset strlen"
    else
        expected_undefined="free malloc memchr memcmp memcpy memset"
    fi

    bc="$tmpdir/bc"; mkdir -p "$bc"
    local cflags="--target=${triple} -emit-llvm -O2 -fno-stack-protector -ffreestanding -fno-builtin -DNDEBUG -DHAVE_CONFIG_H -DPCRE2_CODE_UNIT_WIDTH=8 ${extra_include} -I ${src}/src"
    fail=0
    for s in $PCRE2_SOURCES; do
        if ! clang $cflags -c "${src}/src/${s}.c" -o "$bc/${s}.bc" 2>"$bc/${s}.err"; then
            echo "ERROR: Failed to compile ${s}.c for ${rid}:" >&2
            grep -m1 "error:" "$bc/${s}.err" >&2 || head -3 "$bc/${s}.err" >&2
            fail=1
        fi
    done
    if [ "$rid" = "win-x64" ]; then
        if ! clang $cflags -c "$tmpdir/ashes_pcre2_shims.c" -o "$bc/ashes_pcre2_shims.bc" 2>"$bc/shims.err"; then
            echo "ERROR: Failed to compile libc shims for ${rid}:" >&2
            head -3 "$bc/shims.err" >&2
            fail=1
        fi
    fi
    if [ "$fail" -ne 0 ]; then
        echo "ERROR: PCRE2 bitcode build for ${rid} failed." >&2
        exit 1
    fi

    llvm-link "$bc"/*.bc -o "$tmpdir/all.bc"
    opt -passes='internalize,globaldce' -internalize-public-api-list="$PCRE2_KEEP" "$tmpdir/all.bc" -o "$output_path"

    # Guard the invariant the backend relies on: the payload only leaves external the leaf functions
    # the program module already provides -- malloc/free (the emitted PCRE2 region allocator),
    # memcpy/memset/memcmp/strlen (backend builtins), and, on Linux, memchr (libc import). Anything
    # else would fail to link into a hermetic executable.
    local undefined
    undefined="$(llvm-nm --undefined-only "$output_path" | awk '{print $2}' | sort -u | tr '\n' ' ' | sed 's/ *$//')"
    if [ "$undefined" != "$expected_undefined" ]; then
        echo "ERROR: Unexpected external symbols in ${rid} payload: { ${undefined} }" >&2
        echo "       Expected exactly { ${expected_undefined} }. Update the shims or keep-list." >&2
        exit 1
    fi

    write_version_marker "$rid"
    echo "  -> $output_path ($(stat -c%s "$output_path") bytes; external: ${undefined})"

    rm -rf "$tmpdir"
    trap - RETURN
}

for target in "${TARGETS[@]}"; do
    build_pcre2_bitcode "$target"
done

echo
echo "PCRE2 payloads are available under:"
for target in "${TARGETS[@]}"; do
    echo "  $RUNTIMES_DIR/$target/libpcre2.bc"
done
