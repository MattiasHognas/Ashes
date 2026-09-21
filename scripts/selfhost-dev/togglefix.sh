#!/bin/bash
# usage: togglefix.sh <fixture> <grep-pattern> ; lowers a shared fixture with the archive compiler under each
# contract toggle and prints the lines matching the pattern, so the toggle that restores the old output names the
# stage-0 change responsible.
. "$(dirname "$0")/env.sh"
name=$1; pat=$2
mkdir -p "$J/togglefix"
cp "$R/selfhost/parity/semantics/lowered-ir/$name.source" "$J/togglefix/$name.ash"
for toggle in _NONE ASHES_NO_GENERAL_RC GRC_NO_CALL GRC_NO_TCO GRC_NO_LISTS GRC_NO_TUPLE GRC_NO_SUFFIX GRC_NO_KEEP \
              GRC_NO_BORROW GRC_NO_ARGRET GRC_NO_NOTRANSFER GRC_NO_FRESHARM GRC_NO_DROPROUTE GRC_NO_CTORFRESH \
              GRC_NO_BACKEDGERC GRC_NO_RECUPDFRESH GRC_NO_TUPLEPARTS GRC_NO_AGGRETSLOT GRC_NO_MIXEDLIST GRC_NO_VARIANTPARAM; do
    env "$toggle=1" "$J/cli-tip/ashes" compile "$J/togglefix/$name.ash" -o "$J/togglefix/bin" --emit-ir lowered > /dev/null 2> "$J/togglefix/$name.$toggle.ir"
    printf '%-22s %s\n' "$toggle" "$(grep -m${COUNT:-1} -E "$pat" "$J/togglefix/$name.$toggle.ir" | tr -s ' ' | tr '\n' '|' | cut -c1-120)"
done
