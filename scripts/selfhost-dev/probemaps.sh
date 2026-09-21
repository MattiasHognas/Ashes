#!/bin/bash
# usage: probemaps.sh <stage1> <seconds> ; runs the module probe under gdb, interrupts it at <seconds> and prints
# its memory mappings: where the reference-counted region, the arenas and the stack lie, and how large each is.
. "$(dirname "$0")/env.sh"
bin=$(readlink -f "$1"); at=$2
printf 'import AshesCompiler.Backend.IrCodegen\nimport Ashes.IO as io\n\nio.print("probe")\n' > "$J/probe2/src/Main.ash"
rm -rf "$J/probe2/out"
{
    echo "set pagination off"
    echo "run"
    echo "info proc mappings"
    echo "kill"
} > "$T/probemaps.gdb"
( sleep "$at"; pkill -INT -f "^$bin compile" ) &
systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 bash -c "ulimit -s 1048576; gdb -q -batch -x '$T/probemaps.gdb' --args '$bin' compile --project '$J/probe2/ashes.json'" > "$T/probemaps.log" 2>&1
grep -E "^ *0x[0-9a-f]+ +0x" "$T/probemaps.log" > "$T/probemaps.txt"
echo "mappings: $(wc -l < "$T/probemaps.txt")"
awk '{ size = strtonum($3); printf "%s %s %10.1f MB %s %s\n", $1, $2, size / 1048576, $5, $6 }' "$T/probemaps.txt" | sort -k3 -n -r | head -${LINES_SHOWN:-14}
