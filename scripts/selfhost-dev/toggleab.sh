#!/bin/bash
# usage: toggleab.sh <file.ash> ; compiles the program with the published branch compiler once per contract
# toggle and runs it under an 8G cgroup: the toggle under which the exit status or peak RSS returns to main's
# names the change responsible.
. "$(dirname "$0")/env.sh"
f=$1
for toggle in _NONE ASHES_NO_GENERAL_RC GRC_NO_CALL GRC_NO_TCO GRC_NO_LISTS GRC_NO_TUPLE GRC_NO_SUFFIX GRC_NO_KEEP \
              GRC_NO_BORROW GRC_NO_ARGRET GRC_NO_NOTRANSFER GRC_NO_FRESHARM GRC_NO_DROPROUTE GRC_NO_CTORFRESH \
              GRC_NO_BACKEDGERC GRC_NO_RECUPDFRESH GRC_NO_TUPLEPARTS GRC_NO_AGGRETSLOT GRC_NO_MIXEDLIST GRC_NO_VARIANTPARAM; do
    rm -f "$J/toggleab.bin"
    env "$toggle=1" "$J/cli-branch/ashes" compile "$f" -o "$J/toggleab.bin" > /dev/null 2>&1
    built=$?
    systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 /usr/bin/time -f '%M' -o "$J/toggleab.time" "$J/toggleab.bin" > "$J/toggleab.out" 2>/dev/null
    code=$?
    printf '%-22s compile=%s exit=%-3s %7s MB out=%s\n' "$toggle" "$built" "$code" "$(awk '{printf "%.1f", $1/1024}' "$J/toggleab.time" | tail -1)" "$(head -1 "$J/toggleab.out" | cut -c1-30)"
done
