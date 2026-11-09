#!/bin/bash
# usage: semabisect.sh <commit>... ; for each commit: checkout, Release CLI build, semantics suite only. Restores the branch.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
B=$(git rev-parse --abbrev-ref HEAD)
for c in "$@"; do
    git checkout -q --detach "$c" || exit 1
    dotnet build -c Release src/Ashes.Cli > /dev/null 2>&1 || { echo "$c BUILD FAIL"; continue; }
    echo "$c $(bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/suites.sh semantics | tail -1)"
done
git checkout -q "$B"
dotnet build -c Release src/Ashes.Cli > /dev/null 2>&1
echo BISECT-DONE
