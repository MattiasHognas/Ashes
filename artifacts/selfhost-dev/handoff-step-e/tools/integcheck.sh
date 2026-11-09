#!/bin/bash
# Integration check on selfhost/step-e-leaks: build, format, fuzzing tests, then (behind the heavy lock)
# the six self-hosted suites, whole-program parity and the fixture regeneration check.
cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
W=artifacts/selfhost-dev/handoff-step-e/work
echo "== build"
if ! bash /home/mattias/.claude/jobs/heavy2.sh dotnet build Ashes.slnx > $W/integ-build.log 2>&1; then
    grep -E " error " $W/integ-build.log | sort -u
    echo "BUILD FAILED: stopping before the suites"
    exit 1
fi
grep -E "rror\(s\)|arning\(s\)" $W/integ-build.log | sort -u
if ! dotnet build -c Release src/Ashes.Cli > $W/integ-build-release.log 2>&1; then
    grep -E " error " $W/integ-build-release.log | sort -u
    echo "RELEASE BUILD FAILED: stopping before the suites"
    exit 1
fi
echo "== format"
dotnet format Ashes.slnx --verify-no-changes 2>&1 | tail -2
echo "== fuzzing tests"
systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run --project src/Ashes.Fuzzing.Tests -- --no-progress 2>&1 | grep -E "^\s*(total|failed):"
bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=18G -p MemorySwapMax=0 bash -c 'bash artifacts/selfhost-dev/handoff-step-e/tools/perfix.sh; echo "== regenfixtures"; bash scripts/selfhost-dev/regenfixtures.sh 2>&1 | tail -3; git status --short'
