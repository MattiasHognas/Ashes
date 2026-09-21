#!/bin/bash
# usage: plateautoggles.sh <template.ash> <rounds> ; the template's peak memory at one round count under each
# contract switch. The switch that changes the peak names the mechanism involved.
. "$(dirname "$0")/env.sh"
t=$1; n=$2
for toggle in _NONE ASHES_NO_GENERAL_RC GRC_NO_CALL GRC_NO_TCO GRC_NO_LISTS GRC_NO_TUPLE GRC_NO_SUFFIX GRC_NO_KEEP \
              GRC_NO_BORROW GRC_NO_ARGRET GRC_NO_NOTRANSFER GRC_NO_FRESHARM GRC_NO_DROPROUTE GRC_NO_CTORFRESH \
              GRC_NO_BACKEDGERC GRC_NO_RECUPDFRESH GRC_NO_TUPLEPARTS GRC_NO_AGGRETSLOT GRC_NO_MIXEDLIST GRC_NO_VARIANTPARAM; do
    printf '%-22s %s\n' "$toggle" "$(TOGGLE="$toggle=1" bash "$HERE/plateau.sh" "$t" "$n" | cut -c16-)"
done
