#!/bin/bash
# usage: shapeat.sh <scratch-logs-dir> <size> [extra env...] ; REACH_SHAPE census over that dir's probe core
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
L=$1; size=$2; shift 2
W=$R/artifacts/selfhost-dev/handoff-step-e/work
read -r lo hi < <(grep "\[stack\]" "$L/probe.maps" | awk '{ sub(/^0x0*/, "", $1); sub(/^0x0*/, "", $2); print $1, $2 }')
env REACH_SHAPE="$size" "$@" "$R/artifacts/selfhost-dev/logs/reachcensus" "$L/probe.core" "$lo" "$hi" > "$W/shapeat$size.txt"
echo "--- shapes of size $size roots (first word, second word)"
grep -a '^shape root' "$W/shapeat$size.txt" | awk '{print $3, $4}' | sort | uniq -c | sort -rn | head -"${TOPN:-15}"
