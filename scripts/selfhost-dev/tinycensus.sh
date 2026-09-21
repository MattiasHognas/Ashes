#!/bin/bash
# usage: tinycensus.sh <stage1-binary> <src.ash> [chunks] ; compiles one small file with stage 1 under gdb,
# stops at process exit and dumps the RC region (256 MB chunks, default 2) into s2/tiny-dump/, then
# prints the size census and the leaked-state census. Everything live at exit is a leak.
. "$(dirname "$0")/env.sh"
bin=$1; src=$2; chunks=${3:-2}
D=$T/tiny-dump
rm -rf "$D"; mkdir -p "$D"
base=$((0x100000000000)); chunk=$((256 * 1048576))
{
    echo "set pagination off"
    echo "catch syscall exit exit_group"
    echo "run"
    for k in $(seq 0 $((chunks - 1))); do
        echo "dump binary memory $D/full-$k.bin $((base + k * chunk)) $((base + (k + 1) * chunk))"
    done
    echo "kill"
} > "$T/tinycensus.gdb"
bash -c "ulimit -s 1048576; gdb -q -batch -x '$T/tinycensus.gdb' --args '$bin' compile '$src' -o '$J/tiny.out'" > "$T/tinycensus.log" 2>&1
ls "$D"/full-*.bin > /dev/null 2>&1 || { echo "dump failed"; tail -5 "$T/tinycensus.log"; exit 1; }
census_tool rccensus2 && census_tool rcstates && ir_kinds_table || exit 1
"$T/rccensus2" "$D/full-0.bin" | head -${CENSUS_LINES:-14}
"$T/rcstates" "$D" "$T/irkinds.txt" | head -${STATE_LINES:-16}
