#!/bin/bash
# usage: topclaims.sh <scratch-logs-dir> <size> ; the roots of one size in that dir's probe core, by exclusive claim
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
L=$1; size=$2
W=$R/artifacts/selfhost-dev/handoff-step-e/work
read -r lo hi < <(grep "\[stack\]" "$L/probe.maps" | awk '{ sub(/^0x0*/, "", $1); sub(/^0x0*/, "", $2); print $1, $2 }')
REACH_TOP="$size" "$R/artifacts/selfhost-dev/logs/reachcensus" "$L/probe.core" "$lo" "$hi" > "$W/top$size.txt"
grep -A6 "roots of size $size" "$W/top$size.txt"
grep "top root" "$W/top$size.txt" | sort -k6 -rn | head -"${3:-8}"
