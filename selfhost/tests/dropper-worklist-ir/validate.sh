#!/usr/bin/env bash
set -euo pipefail
cli=$(realpath "${1:?usage: validate.sh /path/to/ashes [/path/to/candidate/StructuralDroppers.ash]}")
candidate=${2:-selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash}
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
bash selfhost/tests/dropper-worklist-ir/render.sh "$candidate"
results=work/dropper-worklist-ir-results
mkdir -p "$results"
run() {
    local memory=$1 timing=$2
    shift 2
    bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q \
        -p "MemoryMax=$memory" -p MemorySwapMax=0 /usr/bin/time -v -o "$timing" "$@"
}
for version in baseline candidate; do
    fixture=work/dropper-worklist-ir-$version
    run 8G "$results/$version-format.time" "$cli" fmt "$fixture" -w >"$results/$version-format.log" 2>&1
    for flavor in plain poison; do
        flags=(ASHES_RC_LEAKCHECK=1)
        if [[ $flavor == poison ]]; then flags+=(ASHES_RC_POISON=1); fi
        run 8G "$results/$version-$flavor-compile.time" env -u ASHES_RC_POISON "${flags[@]}" "$cli" compile \
            --project "$fixture/ashes.json" -o "$fixture/$flavor" >"$results/$version-$flavor-compile.log" 2>&1
        for mode in check ir; do
            run 4G "$results/$version-$flavor-$mode.time" "$fixture/$flavor" "$mode" \
                >"$results/$version-$flavor-$mode.stdout" 2>"$results/$version-$flavor-$mode.stderr"
        done
    done
done
cmp "$results/baseline-plain-ir.stdout" "$results/candidate-plain-ir.stdout"
cmp "$results/candidate-plain-ir.stdout" "$results/candidate-poison-ir.stdout"
cmp "$results/baseline-plain-ir.stdout" "$results/baseline-poison-ir.stdout"
cmp "$results/baseline-plain-check.stdout" "$results/candidate-plain-check.stdout"
for version in baseline candidate; do
    for flavor in plain poison; do
        [[ $(cat "$results/$version-$flavor-check.stderr") == 'ashes-rc-live 4' ]]
        [[ $(cat "$results/$version-$flavor-ir.stderr") == 'ashes-rc-live 2' ]]
        cmp "$results/baseline-plain-check.stdout" "$results/$version-$flavor-check.stdout"
    done
done
printf 'Dropper and copier IR identical; assertions pass plain and poisoned.\n'
