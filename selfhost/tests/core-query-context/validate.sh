#!/usr/bin/env bash
set -euo pipefail
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
cli=$(realpath "${1:?usage: validate.sh /absolute/path/to/ashes}")
results=work/core-query-context/results
mkdir -p "$results"
run() { bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax="$1" -p MemorySwapMax=0 /usr/bin/time -v -o "$2" "${@:3}"; }
bash selfhost/tests/core-query-context/render.sh
run 8G "$results/format.time" "$cli" fmt work/core-query-context -w >"$results/format.log" 2>&1
for flavor in plain poison; do
 flags=(ASHES_RC_LEAKCHECK=1)
 if [[ $flavor == poison ]]; then flags+=(ASHES_RC_POISON=1); fi
 run 8G "$results/$flavor-compile.time" env "${flags[@]}" "$cli" compile --project work/core-query-context/ashes.json -o "work/core-query-context/$flavor" >"$results/$flavor-compile.log" 2>&1
 for mode in check fresh1000 fresh10000 warm1000 warm10000 warm100000 baseline100000; do
  run 4G "$results/$flavor-$mode.time" "work/core-query-context/$flavor" "$mode" >"$results/$flavor-$mode.stdout" 2>"$results/$flavor-$mode.stderr"
 done
done
cmp "$results/plain-fresh1000.stderr" "$results/plain-fresh10000.stderr"
cmp "$results/poison-fresh1000.stderr" "$results/poison-fresh10000.stderr"
cmp "$results/plain-warm1000.stderr" "$results/plain-warm10000.stderr"
cmp "$results/poison-warm1000.stderr" "$results/poison-warm10000.stderr"
cmp "$results/plain-warm100000.stdout" "$results/plain-baseline100000.stdout"
