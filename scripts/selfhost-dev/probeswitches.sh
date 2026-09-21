#!/bin/bash
# usage: probeswitches.sh <SWITCH>... ; builds stage 1 once per environment switch (NAME, set to 1 for the stage-0
# compile) and runs the module probe with each: which stage-0 rule moves stage 1's peak memory. About four minutes
# per switch, one at a time.
. "$(dirname "$0")/env.sh"
for toggle in "$@"; do
    bash "$HERE/quickstage1.sh" "sw-$toggle" "$toggle=1" | tail -2 | tr '\n' ' '
    echo
    bash "$HERE/probepeak.sh" "$J/s1-sw-$toggle" | tail -1
done
