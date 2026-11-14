#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)
cd -- "$root"
compiler=$(realpath -- "${1:?provide compiler path}")
tag=${2:?provide evidence tag}
poison=${3:-plain}
case "$poison" in
    plain) unset ASHES_RC_POISON ;;
    poison) export ASHES_RC_POISON=1 ;;
    *) printf 'expected plain or poison\n' >&2; exit 1 ;;
esac
case "$tag" in *[!a-zA-Z0-9_-]*) printf 'invalid evidence tag\n' >&2; exit 1 ;; esac
export ASHES_RC_LEAKCHECK=1 UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
heavy=${ASHES_HEAVY_RUNNER:-/home/mattias/.claude/jobs/heavy2.sh}
evidence="work/generic-tuple-byte-parts-$tag"
mkdir -p "$evidence"
sha256sum "$compiler" > "$evidence/compiler.sha256"
git rev-parse HEAD > "$evidence/source-commit"
bash selfhost/tests/generic-tuple-byte-parts/render-mirror.sh
for suite in main mirror; do
    case "$suite" in
        main) project=selfhost/tests/generic-tuple-byte-parts/ashes.json; expected='tuple byte parts passed' ;;
        mirror) project=work/generic-tuple-byte-parts-mirror/ashes.json; expected='tuple byte parts mirror passed' ;;
    esac
    bash "$heavy" systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
        /usr/bin/time -v -o "$evidence/$suite-compile.time" "$compiler" compile --project "$project" -o "$evidence/$suite" \
        > "$evidence/$suite-compile.stdout" 2> "$evidence/$suite-compile.stderr"
    for count in 1 1000 10000; do
        bash "$heavy" systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
            /usr/bin/time -v -o "$evidence/$suite-$count.time" "$evidence/$suite" "$count" \
            > "$evidence/$suite-$count.stdout" 2> "$evidence/$suite-$count.stderr"
        test "$(cat "$evidence/$suite-$count.stdout")" = "$expected"
        awk '/^ashes-rc-live [0-9]+$/ { found++; value=$2 } END { if (found != 1) exit 1; print value }' \
            "$evidence/$suite-$count.stderr" > "$evidence/$suite-$count.counter"
    done
    cmp "$evidence/$suite-1000.counter" "$evidence/$suite-10000.counter"
    printf '%s passed, live cells: %s / %s / %s\n' "$suite" \
        "$(cat "$evidence/$suite-1.counter")" "$(cat "$evidence/$suite-1000.counter")" "$(cat "$evidence/$suite-10000.counter")"
done
