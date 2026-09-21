#!/bin/bash
# usage: plateauold.sh <template.ash> <rounds>... ; plateau.sh with every placement rule added for the stage-1 leak
# work switched off (leakwork_switches in env.sh), which is how the compiler behaved before it: the reference point
# for a reproducer.
. "$(dirname "$0")/env.sh"
leakwork_switches_off
bash "$HERE/plateau.sh" "$@"
