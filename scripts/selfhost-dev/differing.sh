#!/bin/bash
# After parityonly.sh: the instruction-level difference size of every program the parity suite reported, using the
# already-built dump tool. SHOW=n prints the first n diff lines of each.
. "$(dirname "$0")/env.sh"
names=$(grep -E "differs at line" "$T/parity.log" | cut -d' ' -f1 | tr '\n' ' ')
SHOW=${SHOW:-0} bash "$HERE/s1diff.sh" $names | { if [ "${SHOW:-0}" = 0 ]; then grep "^=="; else cat; fi; }
