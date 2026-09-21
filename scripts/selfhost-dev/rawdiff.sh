#!/bin/bash
# usage: rawdiff.sh <fixture> [n] ; stage 0 fixture against stage 1's last dumped IR with only locations stripped,
# so numbering differences (temps, locals, labels) show.
. "$(dirname "$0")/env.sh"
name=$1; n=${2:-16}
sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$R/selfhost/parity/semantics/lowered-ir/$name.ir" | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip' > "$J/dumpir/$name.s0.raw"
sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$J/dumpir/$name.s1" > "$J/dumpir/$name.s1.raw"
diff "$J/dumpir/$name.s0.raw" "$J/dumpir/$name.s1.raw" | head -"$n" | cut -c1-160
