#!/bin/bash
# Combined round check: integfull.sh, then a leak-counting stage-1 build and the exit census.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
T=$(git rev-parse --short HEAD)
H=/home/mattias/.claude/jobs/heavy2.sh
bash artifacts/selfhost-dev/handoff-step-e/tools/integfull.sh
echo "== worker reproducers"
bash artifacts/selfhost-dev/handoff-step-e/tools/repros.sh "$R/artifacts/selfhost-dev/s1-tip$T" artifacts/selfhost-dev/handoff-step-e/work/round-repros.txt
echo "== exit census"
echo "== leak-counting stage-1 build"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh lc$T ASHES_RC_LEAKCHECK=1 2>&1 | grep -E "exit=|FAIL"
bash artifacts/selfhost-dev/handoff-step-e/tools/exitcensus.sh "$R/artifacts/selfhost-dev/s1-lc$T" "$R/artifacts/selfhost-dev/handoff-step-e/work/exit-$T"
echo "== ROUND CHECK DONE $(date)"
