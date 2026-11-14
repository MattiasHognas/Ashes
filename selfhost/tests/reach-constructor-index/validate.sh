#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)
cd -- "$root"
compiler=$(realpath -- "${1:?provide compiler path}")
tag=${2:?provide evidence tag}
seeds=${3:-20000}
case "$tag" in *[!a-zA-Z0-9_-]*) printf 'invalid evidence tag\n' >&2; exit 1 ;; esac
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
evidence="work/reach-constructor-index-$tag"
mkdir -p "$evidence"
scope() { local cap=$1; shift; systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 "$@"; }
sha256sum "$compiler" > "$evidence/compiler.sha256"
git rev-parse HEAD > "$evidence/source-commit"
project=$(bash selfhost/tests/reach-constructor-index/render.sh)
scope 8G /usr/bin/time -v -o "$evidence/compile.time" "$compiler" compile --project "$project" -o "$evidence/check" \
    > "$evidence/compile.stdout" 2> "$evidence/compile.stderr"
scope 4G timeout 900 "$evidence/check" check 1 "$seeds" | tee "$evidence/check.stdout"
grep -q '^ok comparisons=' "$evidence/check.stdout"

# Old and new must agree on the checksum of every benchmark shape; the timings are the evidence.
for shape in 'lookup 1500 12000' 'lookup 3000 24000' 'summaries 1500 3000' 'summaries 3000 6000'; do
    name=${shape// /-}
    for mode in old new; do
        scope 4G timeout 900 /usr/bin/time -f '%e s %M KiB' -o "$evidence/$mode-$name.time" \
            "$evidence/check" $mode-$shape > "$evidence/$mode-$name.stdout"
    done
    cmp "$evidence/old-$name.stdout" "$evidence/new-$name.stdout"
    printf '%s: old %s, new %s\n' "$name" "$(cat "$evidence/old-$name.time")" "$(cat "$evidence/new-$name.time")"
done
printf 'frozen constructor lookup oracle passed\n'
