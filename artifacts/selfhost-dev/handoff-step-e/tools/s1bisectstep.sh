#!/bin/bash
# git bisect run step: builds stage 1 at HEAD and compiles work/bisect-crash.ash with it; good when it compiles
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
W=$R/artifacts/selfhost-dev/handoff-step-e/work
cd "$R" || exit 125
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
h=$(git rev-parse --short HEAD)
echo "-- $(date +%H:%M:%S) step $h"
bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh bis$h
[ -x "$R/artifacts/selfhost-dev/s1-bis$h" ] || exit 125
bash -c "ulimit -s 1048576; systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 '$R/artifacts/selfhost-dev/s1-bis$h' compile '$W/bisect-crash.ash' -o '$W/bisect-crash.bin'" > "$W/bisect-$h.log" 2>&1
code=$?
echo "$h compile exit=$code"
[ $code -eq 0 ] && exit 0
exit 1
