#!/usr/bin/env bash
set -euo pipefail
cli=$(realpath "${1:?usage: validate.sh /absolute/path/to/ashes}")
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
perl selfhost/tests/dropper-worklist/render.pl
base=$PWD/work/dropper-worklist
run() {
    local budget=$1 timing=$2
    shift 2
    bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p "MemoryMax=$budget" -p MemorySwapMax=0 /usr/bin/time -v -o "$timing" "$@"
}
run 8G "$base/format.time" "$cli" fmt "$base" -w
for flavor in plain poison; do
    flags=(ASHES_RC_LEAKCHECK=1)
    if [[ $flavor == poison ]]; then flags+=(ASHES_RC_POISON=1); fi
    run 8G "$base/$flavor-compile.time" env -u ASHES_RC_POISON "${flags[@]}" "$cli" compile --project "$base/ashes.json" -o "$base/$flavor" > "$base/$flavor-compile.log" 2>&1
    run 4G "$base/$flavor-check.time" "$base/$flavor" check > "$base/$flavor-check.stdout" 2> "$base/$flavor-check.stderr"
    cat "$base/$flavor-check.stdout" "$base/$flavor-check.stderr"
    for mode in fresh warm; do
        for count in 1000 10000; do
            run 4G "$base/$flavor-$mode-$count.time" "$base/$flavor" "$mode" "$count" > "$base/$flavor-$mode-$count.stdout" 2> "$base/$flavor-$mode-$count.stderr"
            cat "$base/$flavor-$mode-$count.stdout" "$base/$flavor-$mode-$count.stderr"
        done
        cmp "$base/$flavor-$mode-1000.stderr" "$base/$flavor-$mode-10000.stderr"
    done
    for depth in 16 32 64; do
        for version in old new; do
            run 4G "$base/$flavor-linear-$version-depth$depth.time" "$base/$flavor" linear-scale "$version" "$depth" 10000 > "$base/$flavor-linear-$version-depth$depth.stdout" 2> "$base/$flavor-linear-$version-depth$depth.stderr"
        done
        cmp "$base/$flavor-linear-old-depth$depth.stdout" "$base/$flavor-linear-new-depth$depth.stdout"
        cmp "$base/$flavor-linear-old-depth$depth.stderr" "$base/$flavor-linear-new-depth$depth.stderr"
    done
    for count in 1000 10000; do
        for version in old new; do
            run 4G "$base/$flavor-applied-$version-$count.time" "$base/$flavor" applied-scale "$version" 64 "$count" > "$base/$flavor-applied-$version-$count.stdout" 2> "$base/$flavor-applied-$version-$count.stderr"
        done
        cmp "$base/$flavor-applied-old-$count.stdout" "$base/$flavor-applied-new-$count.stdout"
        cmp "$base/$flavor-applied-old-$count.stderr" "$base/$flavor-applied-new-$count.stderr"
    done
    cmp "$base/$flavor-applied-new-1000.stderr" "$base/$flavor-applied-new-10000.stderr"
    for depth in 6 10 14; do
        for version in old new; do
            run 4G "$base/$flavor-$version-depth$depth.time" "$base/$flavor" scale "$version" "$depth" 20 > "$base/$flavor-$version-depth$depth.stdout" 2> "$base/$flavor-$version-depth$depth.stderr"
        done
        cmp "$base/$flavor-old-depth$depth.stdout" "$base/$flavor-new-depth$depth.stdout"
        cmp "$base/$flavor-old-depth$depth.stderr" "$base/$flavor-new-depth$depth.stderr"
    done
done
# Comparing two missing counters must not count as a flat lifetime result.
for flavor in plain poison; do
    for counter in "$base/$flavor"-*.stderr; do
        expected=2
        if [[ $counter == "$base/$flavor-check.stderr" ]]; then expected=3; fi
        if [[ $(cat "$counter") != "ashes-rc-live $expected" ]]; then
            printf 'Unexpected or missing live-cell counter: %s\n' "$counter" >&2
            exit 1
        fi
    done
done
printf 'Decisions, path statuses, scaling, and strict live-cell plateaus passed.\n'
