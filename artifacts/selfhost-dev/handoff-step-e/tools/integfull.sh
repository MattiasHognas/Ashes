#!/bin/bash
# Full integration check on the tip: integcheck.sh, then a stage-1 build and the leak census on
# stage 0 + stage 1 (--stage1) and on stage 0 poisoned (--poison).
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
T=$(git rev-parse --short HEAD)
W=artifacts/selfhost-dev/handoff-step-e/work
H=/home/mattias/.claude/jobs/heavy2.sh
bash artifacts/selfhost-dev/handoff-step-e/tools/integcheck.sh
echo "== stage-1 build"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh tip$T
echo "== census stage 0 + stage 1"
bash -c "ulimit -s 1048576; bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run -c Release --no-build --project src/Ashes.Fuzzing -- leak-census --seeds 3 --stage1 $R/artifacts/selfhost-dev/s1-tip$T --artifacts $W/census-s1-$T" > $W/census-s1-$T.log 2>&1
grep -E "^LEAK|leak census:" $W/census-s1-$T.log
echo "== census poisoned"
bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run -c Release --no-build --project src/Ashes.Fuzzing -- leak-census --seeds 3 --poison --artifacts $W/census-poison-$T > $W/census-poison-$T.log 2>&1
grep -E "^LEAK|^FAIL|leak census:" $W/census-poison-$T.log
echo "== Ashes.Tests"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --project src/Ashes.Tests -- --no-progress > $W/ashestests-$T.log 2>&1; grep -E "total:|failed:|succeeded:|skipped:" $W/ashestests-$T.log | tail -4
echo "== Lsp.Tests"
dotnet run --project src/Ashes.Lsp.Tests -- --no-progress 2>&1 | grep -E "total:|failed:|succeeded:" | tail -3
echo "== e2e plain"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --project src/Ashes.Cli -- test tests > $W/e2e-$T.log 2>&1; tail -3 $W/e2e-$T.log
echo "== e2e poisoned"
ASHES_RC_POISON=1 bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --project src/Ashes.Cli -- test tests > $W/e2ep-$T.log 2>&1; tail -3 $W/e2ep-$T.log; grep -E "^FAIL" $W/e2ep-$T.log | head
