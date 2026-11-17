#!/usr/bin/env bash
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 || ! $2 =~ ^[A-Za-z0-9_-]+$ ]]; then
    echo 'usage: validate.sh <compiler-path> <tag> [poison]' >&2
    exit 2
fi
compiler=$(realpath "$1")
tag=$2
poison=0
if [[ $# == 3 ]]; then
    [[ $3 == poison ]] || { echo 'expected poison as the third argument' >&2; exit 2; }
    poison=1
fi
[[ -x $compiler ]] || { echo 'compiler is not executable' >&2; exit 2; }
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
heavy_runner=${ASHES_HEAVY_RUNNER:-/home/mattias/.claude/jobs/heavy2.sh}
run() {
    local budget=$1 timing=$2
    shift 2
    bash "$heavy_runner" systemd-run --user --scope -q -p "MemoryMax=$budget" -p MemorySwapMax=0 /usr/bin/time -v -o "$timing" "$@"
}
failed=0
for mode in state literal; do
    bash selfhost/tests/pending-goal-lifetime/render.sh "$mode"
    base="$PWD/work/pending-goal-lifetime-$mode"
    result="$base/$tag"
    mkdir -p "$result"
    sha256sum "$base/Main.ash" "$compiler" > "$result/inputs.sha256"
    run 8G "$result/compile.time" env ASHES_RC_LEAKCHECK=1 ASHES_RC_POISON="$poison" "$compiler" compile --project "$base/ashes.json" -o "$result/program" > "$result/compile.log" 2>&1
    for count in 1000 10000; do
        run 4G "$result/$count.time" "$result/program" "$count" > "$result/$count.stdout" 2> "$result/$count.stderr"
        [[ $(cat "$result/$count.stdout") == 'direct passed' ]]
        counter=$(sed -n '/^ashes-rc-live [0-9][0-9]*$/p' "$result/$count.stderr")
        [[ $counter =~ ^ashes-rc-live\ [0-9]+$ ]] || { echo "Missing or ambiguous live counter: $mode/$count" >&2; exit 1; }
        printf '%s\n' "$counter" > "$result/$count.counter"
        printf '%s %s: %s\n' "$mode" "$count" "$counter"
    done
    if ! cmp "$result/1000.counter" "$result/10000.counter"; then
        echo "FAIL: $mode grows between 1000 and 10000 rounds" >&2
        failed=1
    fi
done
exit "$failed"
