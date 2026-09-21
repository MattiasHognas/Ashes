#!/bin/bash
# usage: rcstates.sh <snapshot-dir>... ; builds rcstates and the constructor-name table, then runs it per snapshot
. "$(dirname "$0")/env.sh"
clang -O2 -o "$T/rcstates" "$HERE/rcstates.c" || exit 1
# Constructor names of IrInstructionKind in declaration order (the tag is the declaration index).
awk 'NR>=69 && NR<318 && /^    \| [A-Z]/ { n=$2; sub(/\(.*/, "", n); print n }' \
    "$R/selfhost/packages/semantics/src/AshesCompiler/Semantics/IrInstructions.ash" > "$T/irkinds.txt"
echo "constructors: $(wc -l < "$T/irkinds.txt")"
for d in "$@"; do
    echo "== $d"
    "$T/rcstates" "$d" "$T/irkinds.txt"
done
