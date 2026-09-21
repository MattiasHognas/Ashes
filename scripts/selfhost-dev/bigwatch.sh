#!/bin/bash
# usage: bigwatch.sh dump <debug-stage1> <seconds> <GB>         ; snapshot the IrCodegen probe, list leaked states
#        bigwatch.sh watch <debug-stage1> <cell-hex> <seconds> [stops] ; count history of one cell, compact
# The probe is deterministic under gdb, so an address from `dump` is the same cell in `watch` on the
# SAME binary. Watch for at least as long as the dump ran.
. "$(dirname "$0")/env.sh"
mode=$1; bin=$2
if [ "$mode" = dump ]; then
    bash "$HERE/fulldump.sh" "$bin" AshesCompiler.Backend.IrCodegen "$3" "$4" > "$T/bigwatch.chunks" 2>&1
    echo "chunks: $(cat "$T/bigwatch.chunks")"
    rm -rf "$T/big-dump"; mkdir -p "$T/big-dump"
    for f in "$T"/full-*.bin; do [ -e "$f" ] && mv "$f" "$T/big-dump/"; done
    RCSTATES_LIST=1 "$T/rcstates" "$T/big-dump" "$T/irkinds.txt" > "$T/bigwatch.states" 2>&1
    grep -v "^state" "$T/bigwatch.states" | head -8
    echo "== sample leaked states per head class (middle of the address range)"
    for head in MakeClosureStack LoadLocal Borrow StoreLocal; do
        n=$(grep -c "head=$head " "$T/bigwatch.states")
        [ "$n" -gt 0 ] && grep "head=$head " "$T/bigwatch.states" | sed -n "$((n / 2))p"
    done
    exit 0
fi
cell=$3; secs=$4; stops=${5:-60}
printf 'import AshesCompiler.Backend.IrCodegen\nimport Ashes.IO as io\n\nio.print("probe")\n' > "$J/probe2/src/Main.ash"
rm -rf "$J/probe2/out"
{
    echo "set pagination off"; echo "set confirm off"
    echo "watch *(long *)0x$cell"; echo "run"; echo "bt 7"
    for _ in $(seq 1 "$stops"); do echo "continue"; echo "bt 7"; done
    echo "kill"
} > "$T/bigwatch.gdb"
( sleep "$secs"; pkill -INT -f "^$bin compile" ) &
timeout $((secs + 120)) systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 bash -c \
    "ulimit -s 1048576; gdb -q -batch -x '$T/bigwatch.gdb' --args '$bin' compile --project '$J/probe2/ashes.json'" > "$T/bigwatch.log" 2>&1
awk '
/^Old value/ { old=$4 }
/^New value/ { if (line != "") print line; nv=$4; line=sprintf("%s -> %s :", (length(old) > 6 ? "PTR" : old), (length(nv) > 6 ? "PTR" : nv)) }
/^#[0-9]+ / { fn=$2; if ($2 ~ /^0x/) fn=$4; ln=$NF; sub(/.*\//, "", ln); line=line " " fn "@" ln }
END { if (line != "") print line }' "$T/bigwatch.log" | sed 's/CoreLowering.ash://g' | tail -${TAILN:-14}
echo "events: $(grep -c '^Old value' "$T/bigwatch.log")"
