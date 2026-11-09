#!/bin/bash
# usage: shape.sh <size> [extra env...] ; census over the last probe core with REACH_SHAPE=<size>, tallying root shapes
L=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/logs
W=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work
size=$1; shift
read -r lo hi < <(grep "\[stack\]" "$L/probe.maps" | awk '{ sub(/^0x0*/, "", $1); sub(/^0x0*/, "", $2); print $1, $2 }')
env REACH_SHAPE="$size" "$@" "$L/reachcensus" "$L/probe.core" "$lo" "$hi" > "$W/shape$size.txt"
grep -v '^shape root' "$W/shape$size.txt"
echo "--- shapes of size $size roots (first word, second word)"
grep '^shape root' "$W/shape$size.txt" | awk '{print $3, $4}' | sort | uniq -c | sort -rn | head -20
