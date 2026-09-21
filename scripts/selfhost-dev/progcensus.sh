#!/bin/bash
# usage: progcensus.sh <program.ash> [chunks] ; compiles one program with stage 0, runs it under gdb, stops at
# process exit, dumps the RC region (256 MB chunks, default 1) and prints the size census. Everything live at
# exit is a leak. TOGGLE=NAME=1 sets one environment switch for the compile.
. "$(dirname "$0")/env.sh"
src=$(readlink -f "$1"); chunks=${2:-1}
D=$T/prog-dump
rm -rf "$D"; mkdir -p "$D"
cd "$R" || exit 1
if [ -n "$TOGGLE" ]; then export "${TOGGLE?}"; fi
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$src" -o "$D/prog" > "$D/compile.log" 2>&1 || { tail -5 "$D/compile.log"; exit 1; }
base=$((0x100000000000)); chunk=$((256 * 1048576))
{
    echo "set pagination off"
    echo "catch syscall exit exit_group"
    echo "run"
    for k in $(seq 0 $((chunks - 1))); do
        echo "dump binary memory $D/full-$k.bin $((base + k * chunk)) $((base + (k + 1) * chunk))"
    done
    echo "kill"
} > "$D/census.gdb"
gdb -q -batch -x "$D/census.gdb" --args "$D/prog" > "$D/gdb.log" 2>&1
ls "$D"/full-*.bin > /dev/null 2>&1 || { echo "dump failed"; tail -5 "$D/gdb.log"; exit 1; }
census_tool rccensus2 || exit 1
"$T/rccensus2" "$D/full-0.bin" | head -${CENSUS_LINES:-14}
