#!/bin/bash
# probekey.sh <tag> <exact pretty type list> : probe with only the setAssociation copy at those types
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
cd "$R/work/cp/do" || exit 1
"$R/work/cp/waitslot.sh" "$R/work/cp/cap.sh" 16G -E ASHES_RC_LEAKCHECK=1 -E ASHDIAG_CP_ONLY=IrOptimizer_setAssociation -E "ASHDIAG_CP_KEY=$2" dotnet run -c Release --no-build --project ../../../src/Ashes.Cli -- compile --project ashes.json --debug -o do-$1.bin > build-$1.log 2>&1
cd "$R"
for p in lw1 lw2; do work/cp/docount.sh work/cp/do/do-$1.bin work/cp/$p.ash work/cp/dob-$1-$p; done | tr '\n' ' '; echo " [$1: $2]"
