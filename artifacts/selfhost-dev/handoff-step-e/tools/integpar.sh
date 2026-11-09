#!/bin/bash
# usage: integpar.sh [--final]
# Combined round check in two lanes, one per heavy slot. Everything is built once up front, so the lanes
# only run --no-build binaries. Lane A: suites, parity, fixtures, Ashes.Tests, Lsp.Tests, e2e plain and
# poisoned. Lane B: stage-1 build, census --stage1, census --poison, the worker reproducers. With --final
# (the batch that becomes the PR) lane B also builds the leak-counting stage 1 and runs the exit census.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
T=$(git rev-parse --short HEAD)
W=artifacts/selfhost-dev/handoff-step-e/work
TL=artifacts/selfhost-dev/handoff-step-e/tools
H=/home/mattias/.claude/jobs/heavy2.sh
FINAL=$1
stamp() { echo "-- $(date +%H:%M:%S) $*"; }

stamp "prep: build Debug and Release"
if ! bash $H dotnet build Ashes.slnx > $W/integ-build.log 2>&1; then
    grep -E " error " $W/integ-build.log | sort -u; echo "BUILD FAILED"; exit 1
fi
grep -E "rror\(s\)|arning\(s\)" $W/integ-build.log | sort -u
if ! bash $H dotnet build -c Release Ashes.slnx > $W/integ-build-release.log 2>&1; then
    grep -E " error " $W/integ-build-release.log | sort -u; echo "RELEASE BUILD FAILED"; exit 1
fi
stamp "format"
dotnet format Ashes.slnx --verify-no-changes 2>&1 | tail -2
stamp "fuzzing tests"
systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Fuzzing.Tests -- --no-progress 2>&1 | grep -E "^\s*(total|failed):"

laneA() {
    stamp "A: six suites"
    bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/suites.sh
    stamp "A: parity"
    bash scripts/selfhost-dev/parityonly.sh 2>&1 | tail -2
    stamp "A: regenfixtures"
    bash scripts/selfhost-dev/regenfixtures.sh 2>&1 | tail -3
    git status --short
    stamp "A: Ashes.Tests"
    bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Tests -- --no-progress > $W/ashestests-$T.log 2>&1
    grep -E "total:|failed:|succeeded:|skipped:" $W/ashestests-$T.log | tail -4
    stamp "A: Lsp.Tests"
    dotnet run --no-build --project src/Ashes.Lsp.Tests -- --no-progress 2>&1 | grep -E "total:|failed:|succeeded:" | tail -3
    stamp "A: e2e plain"
    bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Cli -- test tests > $W/e2e-$T.log 2>&1
    tail -1 $W/e2e-$T.log
    stamp "A: e2e poisoned"
    ASHES_RC_POISON=1 bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Cli -- test tests > $W/e2ep-$T.log 2>&1
    tail -1 $W/e2ep-$T.log; grep -E "^FAIL" $W/e2ep-$T.log | head
    stamp "A: done"
}

laneB() {
    stamp "B: stage-1 build"
    bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh tip$T
    stamp "B: census stage 0 + stage 1"
    bash -c "ulimit -s 1048576; bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run -c Release --no-build --project src/Ashes.Fuzzing -- leak-census --seeds 3 --stage1 $R/artifacts/selfhost-dev/s1-tip$T --artifacts $W/census-s1-$T" > $W/census-s1-$T.log 2>&1
    grep -E "^LEAK|leak census:" $W/census-s1-$T.log
    stamp "B: census poisoned"
    bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run -c Release --no-build --project src/Ashes.Fuzzing -- leak-census --seeds 3 --poison --artifacts $W/census-poison-$T > $W/census-poison-$T.log 2>&1
    grep -E "^LEAK|^FAIL|leak census:" $W/census-poison-$T.log
    stamp "B: worker reproducers"
    bash $TL/repros.sh "$R/artifacts/selfhost-dev/s1-tip$T" $W/round-repros.txt
    if [ "$FINAL" = "--final" ]; then
        stamp "B: leak-counting stage-1 build"
        bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh lc$T ASHES_RC_LEAKCHECK=1 2>&1 | grep -E "exit=|FAIL"
        stamp "B: exit census"
        bash $TL/exitcensus.sh "$R/artifacts/selfhost-dev/s1-lc$T" "$R/$W/exit-$T"
    fi
    stamp "B: done"
}

laneA > $W/laneA-$T.log 2>&1 &
laneB > $W/laneB-$T.log 2>&1 &
wait
echo "== lane A"; cat $W/laneA-$T.log
echo "== lane B"; cat $W/laneB-$T.log
echo "== ROUND CHECK DONE $(date)"
