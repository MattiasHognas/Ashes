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
evidence="work/containment-batch-$tag"
mkdir -p "$evidence"
sha256sum "$compiler" > "$evidence/compiler.sha256"
git rev-parse HEAD > "$evidence/source-commit"
git diff -- selfhost/packages/semantics/src/AshesCompiler/Semantics/ArgumentTypeContainment.ash \
    selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash > "$evidence/source.diff"
bash "$heavy" systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
    /usr/bin/time -v -o "$evidence/compile.time" "$compiler" compile \
    --project selfhost/tests/argument-type-containment-batch/ashes.json -o "$evidence/check" \
    > "$evidence/compile.stdout" 2> "$evidence/compile.stderr"
bash "$heavy" systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
    /usr/bin/time -v -o "$evidence/oracle.time" "$evidence/check" check \
    > "$evidence/oracle.stdout" 2> "$evidence/oracle.stderr"
test "$(cat "$evidence/oracle.stdout")" = '24576 graph decisions and special cases passed'
for mode in old new; do
    for count in 1 1000 10000; do
        bash "$heavy" systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
            /usr/bin/time -v -o "$evidence/$mode-$count.time" "$evidence/check" "$mode" "$count" \
            > "$evidence/$mode-$count.stdout" 2> "$evidence/$mode-$count.stderr"
        test "$(cat "$evidence/$mode-$count.stdout")" = 'containment batch passed'
        awk '/^ashes-rc-live [0-9]+$/ { found++; value=$2 } END { if (found != 1) exit 1; print value }' \
            "$evidence/$mode-$count.stderr" > "$evidence/$mode-$count.counter"
    done
    cmp "$evidence/$mode-1.counter" "$evidence/$mode-1000.counter"
    cmp "$evidence/$mode-1000.counter" "$evidence/$mode-10000.counter"
    printf '%s passed, live cells: %s / %s / %s\n' "$mode" \
        "$(cat "$evidence/$mode-1.counter")" "$(cat "$evidence/$mode-1000.counter")" "$(cat "$evidence/$mode-10000.counter")"
done
printf 'frozen containment oracle passed\n'
