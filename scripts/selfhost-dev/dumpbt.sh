#!/bin/bash
# usage: dumpbt.sh [-k] <fixture> [words] ; rebuilds the stage-1 dump tool with debug information (-k keeps the
# built one), runs it on one shared fixture under gdb with a large stack, and at a crash prints the faulting
# instruction, its symbol, and the symbols of the return addresses found in the top stack words (the frame chain
# of generated code is not walkable, so the stack is read directly).
. "$(dirname "$0")/env.sh"
keep=0
if [ "$1" = "-k" ]; then keep=1; shift; fi
name=$1; words=${2:-96}
D=$R/selfhost/parity/semantics/lowered-ir
cd "$R" || exit 1
cp "$D/$name.source" "$J/dumpir/$name.ash"
if [ "$keep" = 0 ]; then
    rm -rf "$J/dumpir/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --debug --project "$J/dumpir/ashes.json" > "$T/dumpbt-build.log" 2>&1
    echo "build exit=$?"
fi
BIN=$(ls "$J/dumpir/out/" 2>/dev/null | head -1)
{
    echo "set pagination off"
    echo "run"
    echo "info symbol \$pc"
    echo "x/6i \$pc-16"
    echo "info registers rax rdi rsi rsp"
    echo "x/${words}a \$sp"
} > "$T/dumpbt.gdb"
bash -c "ulimit -s 1048576; gdb -q -batch -x '$T/dumpbt.gdb' --args '$J/dumpir/out/$BIN' '$J/dumpir/$name.ash'" > "$T/dumpbt.log" 2>&1
grep -A8 "SIGSEGV" "$T/dumpbt.log" | cut -c1-200
echo "-- return addresses on the stack:"
grep -E "^0x[0-9a-f]+:" "$T/dumpbt.log" | grep -oE "<[A-Za-z_][^>]*>" | awk '!seen[$0]++' | head -40
