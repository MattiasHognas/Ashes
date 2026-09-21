#!/bin/bash
# usage: corefind.sh <binary> <payload-hex>... ; searches every writable mapping outside the reference-counted
# region of the core probecore.sh wrote for a word equal to each cell's payload or header address: a hand check
# of a cell reachcensus called leaked. Prints how many hits and misses there were, and the hits.
. "$(dirname "$0")/env.sh"
bin=$1; shift
awk '$5 ~ /w/ && $1 !~ /^0x00001/ { print $1, $2 }' "$T/probe.maps" > "$T/corefind.ranges"
{
    echo "set pagination off"
    while read -r lo hi; do
        for payload in "$@"; do
            echo "find /g $lo, $hi - 1, 0x$payload"
            echo "find /g $lo, $hi - 1, 0x$payload - 16"
        done
    done < "$T/corefind.ranges"
} > "$T/corefind.gdb"
gdb -q -batch -x "$T/corefind.gdb" "$bin" "$T/probe.core" > "$T/corefind.log" 2>&1
echo "ranges searched: $(wc -l < "$T/corefind.ranges")  hits: $(grep -c '^0x' "$T/corefind.log")  misses: $(grep -c 'Pattern not found' "$T/corefind.log")"
grep '^0x' "$T/corefind.log" | head -10
