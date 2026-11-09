#!/bin/bash
# usage: classdiff.sh <stage1> ; size/first-word census at exit for loops10 and loops11, the per-function difference by class
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
. scripts/selfhost-dev/env.sh
W=$R/artifacts/selfhost-dev/handoff-step-e/work
for n in 10 11; do
    CENSUS_LINES=400 STATE_LINES=1 bash scripts/selfhost-dev/tinycensus.sh "$1" artifacts/selfhost-dev/tiny/loops$n.ash > "$W/classdiff-$n.txt"
done
echo "done"
