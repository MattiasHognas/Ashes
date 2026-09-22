#!/bin/bash
# usage: progcensus2.sh <template.ash> <rounds> [chunks] ; compiles a ROUNDS template with this checkout's compiler,
# runs it under gdb to its exit, dumps the reference-counted region and prints the size census and the
# leaked-cell shapes: what a reproducer leaves behind per round, by cell size and first word, before any IR is read.
. "$(dirname "$0")/env.sh"
t=$1; n=$2; chunks=${3:-1}
E=$J/progcensus2
rm -rf "$E"; mkdir -p "$E"
cd "$R" || exit 1
sed "s/ROUNDS/$n/" "$t" > "$E/run.ash"
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$E/run.ash" -o "$E/run.bin" > "$E/run.compile" 2>&1 || { echo "compile failed"; tail -3 "$E/run.compile"; exit 1; }
base=$((0x100000000000)); chunk=$((256 * 1048576))
{
    echo "set pagination off"
    echo "catch syscall exit exit_group"
    echo "run"
    for k in $(seq 0 $((chunks - 1))); do
        echo "dump binary memory $E/full-$k.bin $((base + k * chunk)) $((base + (k + 1) * chunk))"
    done
    echo "kill"
} > "$E/census.gdb"
gdb -q -batch -x "$E/census.gdb" --args "$E/run.bin" > "$E/census.log" 2>&1
ls "$E"/full-0.bin > /dev/null 2>&1 || { echo "dump failed"; tail -5 "$E/census.log"; exit 1; }
census_tool rccensus2 && census_tool rcroots || exit 1
"$T/rccensus2" "$E/full-0.bin" | head -${CENSUS_LINES:-14}
echo "-- roots (unreferenced cells) by size and first word:"
"$T/rcroots" "$E/full-0.bin" 2> /dev/null | head -${ROOT_LINES:-12}
