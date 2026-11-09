#!/bin/bash
# stage-1 build of the branch with the leak counter (20G scope), smoke and guards, k22 and c on stage 1, then the full tally (8G scope)
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
cd "$R" || exit 1
tag=$1
"$R/work/cp/cap.sh" 20G /usr/bin/time -f "s1 build+smoke: %e s, peak %M KB" bash scripts/selfhost-dev/quickstage1.sh "$tag" ASHES_RC_LEAKCHECK=1
S1="$R/artifacts/selfhost-dev/s1-$tag"
[ -x "$S1" ] || { echo "no stage-1 binary"; exit 1; }
for t in k22 k18 c; do bash work/cp/s1run.sh $t "$S1" 1 2; done
"$R/work/cp/cap.sh" 8G bash work/q40/fulltally.sh "$S1" "$tag"
