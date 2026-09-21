#!/bin/bash
# usage: s1diffold.sh <fixture>... ; how far stage 1 is from stage 0's lowering of the fixtures AS IT WAS before the
# stage-1 leak work (every placement rule added for it switched off): regenerates the fixtures that way, runs
# s1diff.sh, then regenerates them normally again. Tells a missing mirror of the new rules from an older gap.
. "$(dirname "$0")/env.sh"
(
    leakwork_switches_off
    bash "$HERE/regenfixtures.sh" | tail -1
)
echo "-- stage 1 against the old lowering"
bash "$HERE/s1diff.sh" "$@" | grep "^=="
bash "$HERE/regenfixtures.sh" | tail -1
echo "-- stage 1 against the new lowering"
bash "$HERE/s1diff.sh" "$@" | grep "^=="
