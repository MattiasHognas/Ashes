#!/bin/bash
# usage: s1switch.sh <tag> <ENV=val> ; builds stage 1 at HEAD with the switch, compiles work/bisect-crash.ash with it, then runs the semantics suite with the switch
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
W=$R/artifacts/selfhost-dev/handoff-step-e/work
H=/home/mattias/.claude/jobs/heavy2.sh
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
echo "-- $(date +%H:%M:%S) build s1-$1 with $2"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh "$1" "$2"
bash -c "ulimit -s 1048576; systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 '$R/artifacts/selfhost-dev/s1-$1' compile '$W/bisect-crash.ash' -o '$W/bisect-crash.bin'" > "$W/switch-$1.log" 2>&1
echo "RESULT crash-repro compile exit=$?"
echo "-- $(date +%H:%M:%S) semantics suite with $2"
env "$2" bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/suites.sh semantics
echo "== SWITCH DONE"
