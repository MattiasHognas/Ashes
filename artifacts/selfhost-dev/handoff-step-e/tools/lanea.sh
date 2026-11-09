#!/bin/bash
# usage: lanea.sh ; integpar's lane A alone in one heavy slot (build, format, suites, parity, fixtures, Ashes.Tests, Lsp.Tests, e2e plain and poisoned)
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
T=$(git rev-parse --short HEAD)
W=artifacts/selfhost-dev/handoff-step-e/work
H=/home/mattias/.claude/jobs/heavy2.sh
stamp() { echo "-- $(date +%H:%M:%S) $*"; }
stamp build
bash $H dotnet build Ashes.slnx > $W/la-build.log 2>&1 || { grep -E " error " $W/la-build.log | sort -u; echo "BUILD FAILED"; exit 1; }
bash $H dotnet build -c Release Ashes.slnx > $W/la-build-r.log 2>&1 || { echo "RELEASE BUILD FAILED"; exit 1; }
dotnet format Ashes.slnx --verify-no-changes 2>&1 | tail -2
stamp "Lsp.Tests"
dotnet run --no-build --project src/Ashes.Lsp.Tests -- --no-progress 2>&1 | grep -E "total:|failed:" | tail -2
stamp "e2e plain"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Cli -- test tests > $W/e2e-$T.log 2>&1
tail -1 $W/e2e-$T.log; grep -E "^FAIL|✗" $W/e2e-$T.log | head
stamp "e2e poisoned"
ASHES_RC_POISON=1 bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Cli -- test tests > $W/e2ep-$T.log 2>&1
tail -1 $W/e2ep-$T.log; grep -E "^FAIL|✗" $W/e2ep-$T.log | head
stamp "Ashes.Tests"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Tests -- --no-progress > $W/ashestests-$T.log 2>&1
grep -E "total:|failed:" $W/ashestests-$T.log | tail -2; grep -E "^failed " $W/ashestests-$T.log | head
stamp "six suites"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/suites.sh
stamp parity
bash scripts/selfhost-dev/parityonly.sh 2>&1 | tail -2
stamp "LANE A DONE"
