#!/bin/bash
# usage: progwatch.sh <address-hex> [stops] [frames] ; every write to one 8-byte word while the program progbt.sh
# last built runs under gdb (deterministic addresses), one line per event: old -> new, then the top frames. The
# word to watch comes from the crash: `find /g` in gdb locates a corrupted pointer in the RC region.
. "$(dirname "$0")/env.sh"
addr=$1; stops=${2:-40}; frames=${3:-2}
D=$T/prog-bt
{
    echo "set pagination off"
    echo "set confirm off"
    echo "break _start_main"
    echo "run"
    echo "watch *(long *)0x$addr"
    for _ in $(seq 1 "$stops"); do
        echo "continue"
        echo "bt $frames"
    done
    echo "kill"
} > "$D/watch.gdb"
gdb -q -batch -x "$D/watch.gdb" --args "$D/prog" > "$D/watch.log" 2>&1
awk '
/^Old value/ { old=$4 }
/^New value/ { if (line != "") print line; line=sprintf("%s -> %s :", old, $4) }
/^#[0-9]+ / { fn=$2; if ($2 ~ /^0x/) fn=$4; pc=($2 ~ /^0x/ ? $2 : ""); line=line " " fn "(" pc ")" }
END { if (line != "") print line }' "$D/watch.log" | tail -${TAIL:-12}
