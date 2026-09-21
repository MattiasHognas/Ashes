#!/bin/bash
# usage: s1difffns.sh <fixture>... ; after s1diff.sh has dumped a fixture, the differing line count of each of its
# functions, by the binding it was lowered from: which functions a port still has to reach.
. "$(dirname "$0")/env.sh"
for name in "$@"; do
    echo "== $name"
    for stage in s0 s1; do
        awk -v out="$J/dumpir/$name.$stage.byfn" '
            /^function / { match($0, /from [^]]*\]/); key = substr($0, RSTART + 5, RLENGTH - 6); kind = $3; sub(/^\[/, "", kind) }
            { print key "\t" $0 > out }' "$J/dumpir/$name.$stage.norm"
    done
    cut -f1 "$J/dumpir/$name.s0.byfn" "$J/dumpir/$name.s1.byfn" | sort -u | while read -r key; do
        grep -F "$key	" "$J/dumpir/$name.s0.byfn" | cut -f2- > "$J/dumpir/$name.s0.one"
        grep -F "$key	" "$J/dumpir/$name.s1.byfn" | cut -f2- > "$J/dumpir/$name.s1.one"
        differing=$(diff "$J/dumpir/$name.s0.one" "$J/dumpir/$name.s1.one" | grep -c '^[<>]')
        [ "$differing" != "0" ] && echo "   $key: stage0=$(wc -l < "$J/dumpir/$name.s0.one") stage1=$(wc -l < "$J/dumpir/$name.s1.one") differing=$differing"
    done
done
