#!/usr/bin/env bash
set -euo pipefail

# Run from the repository root with an already built stage-0 CLI.
cli=$(realpath "${1:?usage: validate.sh /absolute/path/to/ashes}")
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
bash selfhost/tests/persistent-dropper-context/render.sh
bash selfhost/tests/persistent-dropper-context/render-synthesis.sh
results=work/persistent-dropper-context/results
mkdir -p "$results"
run() {
    local budget=$1 timing=$2
    shift 2
    bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q \
        -p "MemoryMax=$budget" -p MemorySwapMax=0 /usr/bin/time -v -o "$timing" "$@"
}
for flavor in plain poison; do
    flags=(ASHES_RC_LEAKCHECK=1)
    if [[ $flavor == poison ]]; then flags+=(ASHES_RC_POISON=1); fi
    run 8G "$results/$flavor-compile.time" env "${flags[@]}" "$cli" compile \
        --project work/persistent-dropper-context/ashes.json -o "work/persistent-dropper-context/$flavor" \
        >"$results/$flavor-compile.log" 2>&1
    for mode in check fresh1000 fresh10000 warm10000 warm100000; do
        run 4G "$results/$flavor-$mode.time" "work/persistent-dropper-context/$flavor" "$mode" \
            >"$results/$flavor-$mode.stdout" 2>"$results/$flavor-$mode.stderr"
    done
    cmp "$results/$flavor-fresh1000.stderr" "$results/$flavor-fresh10000.stderr"
    cmp "$results/$flavor-warm10000.stderr" "$results/$flavor-warm100000.stderr"
done
for version in baseline candidate; do
    fixture=work/persistent-dropper-$version
    for flavor in plain poison; do
        flags=(ASHES_RC_LEAKCHECK=1)
        if [[ $flavor == poison ]]; then flags+=(ASHES_RC_POISON=1); fi
        run 8G "$results/$version-$flavor-compile.time" env "${flags[@]}" "$cli" compile \
            --project "$fixture/ashes.json" -o "$fixture/$flavor" >"$results/$version-$flavor-compile.log" 2>&1
        for mode in oracle 1000 10000 100000; do
            run 4G "$results/$version-$flavor-$mode.time" "$fixture/$flavor" "$mode" \
                >"$results/$version-$flavor-$mode.stdout" 2>"$results/$version-$flavor-$mode.stderr"
        done
    done
done
cmp "$results/baseline-plain-oracle.stdout" "$results/candidate-plain-oracle.stdout"
cmp "$results/candidate-plain-oracle.stdout" "$results/candidate-poison-oracle.stdout"
cmp "$results/candidate-plain-1000.stderr" "$results/candidate-plain-10000.stderr"
cmp "$results/candidate-poison-1000.stderr" "$results/candidate-poison-10000.stderr"
