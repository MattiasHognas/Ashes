#!/usr/bin/env bash
# Regenerate or verify the lexer's fixed Unicode classifications from the pinned PCRE2 payload.
# Uses the same memory scopes as the other self-hosting development tools.
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
mode=${1:---write}
case "$mode" in
    --write|--verify) ;;
    *) echo "usage: $0 [--write|--verify]" >&2; exit 2 ;;
esac
version=$(sed -n 's:.*<Pcre2Version>\(.*\)</Pcre2Version>.*:\1:p' "$repo_root/Directory.Build.props")
installed=$(cat "$repo_root/runtimes/linux-x64/pcre2.version")
if [ -z "$version" ] || [ "$installed" != "$version" ]; then
    echo "Refresh the linux-x64 PCRE2 payload to Directory.Build.props before generating classifications." >&2
    exit 1
fi
mkdir -p "$repo_root/artifacts/selfhost-dev"
scratch=$(mktemp -d "$repo_root/artifacts/selfhost-dev/lexer-unicode.XXXXXX")
systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
    clang -O2 -Werror -Wno-override-module \
    "$repo_root/scripts/selfhost-dev/generate-lexer-unicode.c" \
    "$repo_root/runtimes/linux-x64/libpcre2.bc" -o "$scratch/generate"
systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
    "$scratch/generate" "$scratch/reference.bin" "$version" > "$scratch/UnicodeClassification.ash"
destination="$repo_root/selfhost/packages/frontend/src/AshesCompiler/Frontend/UnicodeClassification.ash"
if [ "$mode" = --verify ]; then
    cmp "$scratch/UnicodeClassification.ash" "$destination"
    echo "Unicode classifications match the bundled PCRE2 payload."
else
    cp "$scratch/UnicodeClassification.ash" "$destination"
    echo "Wrote $destination"
fi
echo "Reference byte per code point (letter=1, identifier=2, whitespace=4): $scratch/reference.bin"
