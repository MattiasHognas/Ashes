#!/bin/bash
# usage: s1rawprog.sh <program-name> [n] ; after s1diffprog.sh has dumped a program, the same comparison with temp,
# local and label numbers kept and only locations stripped: what the byte-for-byte parity tests will see.
. "$(dirname "$0")/env.sh"
name=$1; n=${2:-12}
E=$J/diffprog
for stage in s0 s1; do
    sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$E/$name.$stage" | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip' > "$E/$name.$stage.raw"
done
echo "== $name raw differing lines: $(diff "$E/$name.s0.raw" "$E/$name.s1.raw" | grep -c '^[<>]') of $(wc -l < "$E/$name.s0.raw")"
diff "$E/$name.s0.raw" "$E/$name.s1.raw" | head -"$n" | cut -c1-160
