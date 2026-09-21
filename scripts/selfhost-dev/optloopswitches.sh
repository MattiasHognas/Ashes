#!/bin/bash
# usage: optloopswitches.sh <source.ash> <rounds> <SWITCH>... ; rebuilds the optimizer probe with no switch and
# under each environment switch and runs it: which stage-0 rule moves the self-hosted optimizer's leak.
. "$(dirname "$0")/env.sh"
src=$1; n=$2; shift 2
for toggle in _NONE "$@"; do
    printf '%-26s ' "$toggle"
    env "$toggle=1" bash "$HERE/optloop.sh" -b "$src" "$n" | tail -1
done
