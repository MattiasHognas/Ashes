#!/bin/bash
# usage: plateauswitches.sh <template.ash> <rounds> <SWITCH>... ; plateau.sh once with no switch and once under each
# environment switch, plain and with the reference-count poison: which placement rule a wrong result depends on.
. "$(dirname "$0")/env.sh"
t=$1; n=$2; shift 2
for toggle in _NONE "$@"; do
    printf '%-28s ' "$toggle"
    TOGGLE="$toggle=1" bash "$HERE/plateau.sh" "$t" "$n"
    printf '%-28s ' "$toggle+poison"
    ASHES_RC_POISON=1 TOGGLE="$toggle=1" bash "$HERE/plateau.sh" "$t" "$n"
done
