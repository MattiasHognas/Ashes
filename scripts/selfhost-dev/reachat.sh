#!/bin/bash
# usage: reachat.sh <stage1> <seconds> [project.json] ; how much of stage 1's reference-counted heap is leaked at
# a moment: probecore.sh writes a core at <seconds>, reachcensus marks from the stack, static data and arena
# memory into the heap. "Leaked for certain" is a lower bound (a stale word in unused arena memory still counts
# as a pointer; registers are not scanned, which can only hide a handful of cells the other way).
. "$(dirname "$0")/env.sh"
bash "$HERE/probecore.sh" "$@" | head -1
census_tool reachcensus || exit 1
read -r lo hi < <(grep "\[stack\]" "$T/probe.maps" | awk '{ sub(/^0x0*/, "", $1); sub(/^0x0*/, "", $2); print $1, $2 }')
"$T/reachcensus" "$T/probe.core" "$lo" "$hi"
