#!/bin/bash
# usage: rcstates.sh <snapshot-dir>... ; builds rcstates and the constructor-name table, then runs it per snapshot
. "$(dirname "$0")/env.sh"
census_tool rcstates && ir_kinds_table || exit 1
echo "constructors: $(wc -l < "$T/irkinds.txt")"
for d in "$@"; do
    echo "== $d"
    "$T/rcstates" "$d" "$T/irkinds.txt"
done
