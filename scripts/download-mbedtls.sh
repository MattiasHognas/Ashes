#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIMES_DIR="$REPO_ROOT/runtimes"
VERSION="$(sed -n 's:.*<MbedTlsVersion>\(.*\)</MbedTlsVersion>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)"

usage() {
  cat <<USAGE
Usage:
  scripts/download-mbedtls.sh --all
  scripts/download-mbedtls.sh --linux-x64
  scripts/download-mbedtls.sh --linux-arm64
  scripts/download-mbedtls.sh --win-x64
USAGE
}

targets=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --all) targets=(linux-x64 linux-arm64 win-x64) ;;
    --linux-x64) targets+=(linux-x64) ;;
    --linux-arm64) targets+=(linux-arm64) ;;
    --win-x64) targets+=(win-x64) ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

if [ "${#targets[@]}" -eq 0 ]; then
  case "$(uname -m)" in
    x86_64|amd64) targets=(linux-x64) ;;
    aarch64|arm64) targets=(linux-arm64) ;;
    *) echo "unsupported host architecture; pass an explicit target" >&2; exit 2 ;;
  esac
fi

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "missing required tool: $1" >&2; exit 1; }
}

need git
need clang
need llvm-link
need opt

triple_for() {
  case "$1" in
    linux-x64) echo "x86_64-unknown-linux-gnu" ;;
    linux-arm64) echo "aarch64-unknown-linux-gnu" ;;
    # windows-gnu (not -msvc): it needs no MSVC SDK to parse under -nostdinc + stub headers,
    # and yields the same datalayout as windows-msvc (LinkModules2 gates on datalayout, not the
    # triple string), so the payload still matches the backend's win-x64 program module.
    win-x64) echo "x86_64-w64-windows-gnu" ;;
    *) echo "unknown target: $1" >&2; exit 2 ;;
  esac
}

# Declaration-only stub headers so the sources parse under the windows-gnu triple with no MinGW
# sysroot. clang still supplies stddef/stdint/stdarg/limits itself (freestanding); only the libc
# headers Mbed TLS includes are stubbed. None of these are linked from the payload -- they resolve to
# the backend's in-module builtins (memcpy/memset/...) or to PE imports (libc/bcrypt) at link time.
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
char *strstr(const char *, const char *);
int strcmp(const char *, const char *);
int strncmp(const char *, const char *, size_t);
char *strncpy(char *, const char *, size_t);
char *strcpy(char *, const char *);
size_t strnlen(const char *, size_t);
#endif
EOF
  cat > "$dir/stdlib.h" <<'EOF'
#ifndef _ASHES_STUB_STDLIB_H
#define _ASHES_STUB_STDLIB_H
#include <stddef.h>
void *calloc(size_t, size_t);
void *malloc(size_t);
void free(void *);
void exit(int);
int rand(void);
void srand(unsigned int);
char *getenv(const char *);
#endif
EOF
  cat > "$dir/ctype.h" <<'EOF'
#ifndef _ASHES_STUB_CTYPE_H
#define _ASHES_STUB_CTYPE_H
int isalnum(int); int isalpha(int); int isdigit(int); int islower(int);
int isprint(int); int isspace(int); int isupper(int); int isxdigit(int);
int tolower(int); int toupper(int);
#endif
EOF
  cat > "$dir/stdio.h" <<'EOF'
#ifndef _ASHES_STUB_STDIO_H
#define _ASHES_STUB_STDIO_H
#include <stddef.h>
#include <stdarg.h>
typedef struct _ASHES_FILE FILE;
extern FILE *stdout;
extern FILE *stderr;
int printf(const char *, ...);
int fprintf(FILE *, const char *, ...);
int snprintf(char *, size_t, const char *, ...);
int vsnprintf(char *, size_t, const char *, va_list);
int sscanf(const char *, const char *, ...);
FILE *fopen(const char *, const char *);
int fclose(FILE *);
size_t fread(void *, size_t, size_t, FILE *);
size_t fwrite(const void *, size_t, size_t, FILE *);
int fseek(FILE *, long, int);
long ftell(FILE *);
int puts(const char *);
void setbuf(FILE *, char *);
int remove(const char *);
int rename(const char *, const char *);
char *fgets(char *, int, FILE *);
int fputs(const char *, FILE *);
int fputc(int, FILE *);
int fflush(FILE *);
int ferror(FILE *);
int feof(FILE *);
#define SEEK_SET 0
#define SEEK_CUR 1
#define SEEK_END 2
#define EOF (-1)
#endif
EOF
  cat > "$dir/time.h" <<'EOF'
#ifndef _ASHES_STUB_TIME_H
#define _ASHES_STUB_TIME_H
/* 64-bit, NOT long: Windows x64 is LLP64 (long is 32-bit) and msvcrt's time()/gmtime() are the
   64-bit _time64 family there. A 32-bit time_t makes gmtime read past the object and fail, which
   x509 chain verification turns into MBEDTLS_ERR_X509_FATAL_ERROR. */
typedef long long time_t;
struct tm { int tm_sec, tm_min, tm_hour, tm_mday, tm_mon, tm_year, tm_wday, tm_yday, tm_isdst; };
time_t time(time_t *);
struct tm *gmtime(const time_t *);
#endif
EOF
  cat > "$dir/errno.h" <<'EOF'
#ifndef _ASHES_STUB_ERRNO_H
#define _ASHES_STUB_ERRNO_H
extern int errno;
#endif
EOF
  cat > "$dir/assert.h" <<'EOF'
#ifndef _ASHES_STUB_ASSERT_H
#define _ASHES_STUB_ASSERT_H
#define assert(x) ((void)0)
#endif
EOF
  cat > "$dir/inttypes.h" <<'EOF'
#ifndef _ASHES_STUB_INTTYPES_H
#define _ASHES_STUB_INTTYPES_H
#include <stdint.h>
/* Mbed TLS expands MBEDTLS_PRINTF_SIZET to PRIuPTR under MinGW. These only feed debug-print format
   strings; the LLP64 width ("ll") just needs to be a valid string literal so concatenation parses. */
#define PRIuPTR "llu"
#define PRIiPTR "lli"
#define PRIdPTR "lld"
#define PRIu64  "llu"
#define PRId64  "lld"
#define PRIx64  "llx"
#endif
EOF
  # Windows entropy source (entropy_poll.c). Mbed TLS 3.6 draws OS randomness from BCryptGenRandom
  # (bcrypt.dll); these stubs supply just that declaration. windows.h/intsafe.h are otherwise unused
  # here. BCryptGenRandom stays external and is satisfied by a PE import at link time.
  cat > "$dir/windows.h" <<'EOF'
#ifndef _ASHES_STUB_WINDOWS_H
#define _ASHES_STUB_WINDOWS_H
#include <stddef.h>
typedef unsigned long DWORD;
typedef int BOOL;
typedef unsigned short WCHAR;
typedef void *HANDLE;
typedef struct _FILETIME { DWORD dwLowDateTime; DWORD dwHighDateTime; } FILETIME;
void GetSystemTimeAsFileTime(FILETIME *);
#define SecureZeroMemory(ptr, cnt) do { volatile unsigned char *_azp = (volatile unsigned char *)(ptr); size_t _azn = (size_t)(cnt); while (_azn--) { *_azp++ = 0; } } while (0)
/* Declarations for mbedtls_x509_crt_parse_path's Windows directory scan. Ashes never calls
   parse_path (certs are parsed from buffers/files), so globaldce strips all of this from the
   payload; the stubs exist only so x509_crt.c parses. */
#define MAX_PATH 260
#define CP_ACP 0
#define INVALID_HANDLE_VALUE ((HANDLE)(long)-1)
#define FILE_ATTRIBUTE_DIRECTORY 0x10
#define ERROR_NO_MORE_FILES 18
typedef struct _WIN32_FIND_DATAW { DWORD dwFileAttributes; WCHAR cFileName[MAX_PATH]; } WIN32_FIND_DATAW;
HANDLE FindFirstFileW(const WCHAR *, WIN32_FIND_DATAW *);
BOOL FindNextFileW(HANDLE, WIN32_FIND_DATAW *);
BOOL FindClose(HANDLE);
DWORD GetLastError(void);
int MultiByteToWideChar(unsigned int, DWORD, const char *, int, WCHAR *, int);
int WideCharToMultiByte(unsigned int, DWORD, const WCHAR *, int, char *, int, const char *, BOOL *);
/* psa_its_file.c atomic rename on Windows. Reachable (PSA key storage), so it resolves to a
   kernel32 PE import at link time. */
#define MOVEFILE_REPLACE_EXISTING 0x1
BOOL MoveFileExA(const char *, const char *, DWORD);
#endif
EOF
  cat > "$dir/intsafe.h" <<'EOF'
#ifndef _ASHES_STUB_INTSAFE_H
#define _ASHES_STUB_INTSAFE_H
#include <limits.h>
#endif
EOF
  cat > "$dir/bcrypt.h" <<'EOF'
#ifndef _ASHES_STUB_BCRYPT_H
#define _ASHES_STUB_BCRYPT_H
typedef long NTSTATUS;
#define BCRYPT_SUCCESS(s) (((NTSTATUS)(s)) >= 0)
#define BCRYPT_USE_SYSTEM_PREFERRED_RNG 0x00000002
NTSTATUS BCryptGenRandom(void *, unsigned char *, unsigned long, unsigned long);
#endif
EOF
  mkdir -p "$dir/sys"
  cat > "$dir/sys/types.h" <<'EOF'
#ifndef _ASHES_STUB_SYS_TYPES_H
#define _ASHES_STUB_SYS_TYPES_H
#include <stddef.h>
#endif
EOF
  cat > "$dir/sys/stat.h" <<'EOF'
#ifndef _ASHES_STUB_SYS_STAT_H
#define _ASHES_STUB_SYS_STAT_H
#include <stddef.h>
#endif
EOF
}

build_target() {
  local rid="$1"
  local triple
  triple="$(triple_for "$rid")"
  local out_dir="$RUNTIMES_DIR/$rid"
  local tmp
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN

  mkdir -p "$out_dir"
  git clone --depth 1 --branch "v${VERSION}" --recurse-submodules --shallow-submodules \
    https://github.com/Mbed-TLS/mbedtls.git "$tmp/mbedtls"
  local src="$tmp/mbedtls"
  local bc="$tmp/bc"
  mkdir -p "$bc"

  local cflags=(
    -target "$triple"
    -emit-llvm
    -Oz
    -ffunction-sections
    -fdata-sections
    -fvisibility=hidden
    -fno-stack-protector
    -I "$src/include"
    -I "$src/library"
    -I "$src/tf-psa-crypto/include"
    -I "$src/tf-psa-crypto/core"
    -I "$src/tf-psa-crypto/extras"
    -I "$src/tf-psa-crypto/utilities"
    -I "$src/tf-psa-crypto/drivers/builtin/include"
    -I "$src/tf-psa-crypto/drivers/builtin/src"
    -c
  )

  # Windows has no MinGW sysroot here: parse against declaration-only stubs (freestanding, no builtin
  # libc), same as the PCRE2 payload. Compiler builtins (memcpy/memset/...) still come from clang.
  if [ "$rid" = "win-x64" ]; then
    write_win_stub_headers "$tmp/winstub"
    cflags+=(-ffreestanding -fno-builtin -isystem "$tmp/winstub")
    # Without this, LLVM instruction selection lowers bignum's double-width division to a
    # __udivti3 libcall. Linux resolves it from libgcc_s.so.1; Windows has no system provider
    # (and a C-compiled shim gets the MinGW pointer ABI, which mismatches the libcall's i128
    # register ABI), so use Mbed TLS's own escape hatch: bignum avoids 128-bit division entirely.
    cflags+=(-DMBEDTLS_NO_UDBL_DIVISION)
  fi

  local sources=(
    "$src"/library/*.c
    "$src"/tf-psa-crypto/core/*.c
    "$src"/tf-psa-crypto/drivers/builtin/src/*.c
    "$src"/tf-psa-crypto/utilities/*.c
  )

  local file
  for file in "${sources[@]}"; do
    [ -f "$file" ] || continue
    case "$(basename "$file")" in
      mbedtls_config.c) continue ;;
      # Ashes drives I/O through its own BIO callbacks and never calls Mbed TLS's socket or timing
      # helpers, so drop the files that would otherwise pull in <winsock2.h>/<windows.h>.
      net_sockets.c | timing.c) continue ;;
    esac
    clang "${cflags[@]}" "$file" -o "$bc/$(basename "$file" .c).bc"
  done

  llvm-link "$bc"/*.bc -o "$tmp/libmbedtls.full.bc"
  opt -passes='internalize,globaldce' \
    -internalize-public-api-list='mbedtls_ctr_drbg_init,mbedtls_ctr_drbg_seed,mbedtls_ctr_drbg_random,mbedtls_entropy_init,mbedtls_entropy_func,mbedtls_ssl_config_init,mbedtls_ssl_config_defaults,mbedtls_ssl_conf_authmode,mbedtls_ssl_conf_ca_chain,mbedtls_ssl_conf_rng,mbedtls_ssl_conf_own_cert,mbedtls_ssl_init,mbedtls_ssl_setup,mbedtls_ssl_set_hostname,mbedtls_ssl_set_bio,mbedtls_ssl_get_verify_result,mbedtls_ssl_handshake,mbedtls_ssl_read,mbedtls_ssl_write,mbedtls_ssl_close_notify,mbedtls_ssl_free,mbedtls_x509_crt_init,mbedtls_x509_crt_parse,mbedtls_x509_crt_parse_file,mbedtls_x509_crt_free,mbedtls_pk_init,mbedtls_pk_parse_key,mbedtls_pk_free,mbedtls_strerror' \
    "$tmp/libmbedtls.full.bc" -o "$out_dir/libmbedtls.bc"
  printf '%s\n' "$VERSION" > "$out_dir/mbedtls.version"
  echo "built $out_dir/libmbedtls.bc"
}

for target in "${targets[@]}"; do
  build_target "$target"
done
