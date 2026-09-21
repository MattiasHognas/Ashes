#!/bin/bash
# usage: progbt.sh <program.ash> [frames] ; compiles one program with stage 0 and debug information, runs it under
# gdb and prints, at a crash, the backtrace, the faulting instruction and the registers it reads: where a
# use-after-free in a reproducer shows up. TOGGLE=NAME=1 sets one environment switch for the compile.
. "$(dirname "$0")/env.sh"
src=$(readlink -f "$1"); frames=${2:-8}
D=$T/prog-bt
rm -rf "$D"; mkdir -p "$D"
cd "$R" || exit 1
if [ -n "$TOGGLE" ]; then export "${TOGGLE?}"; fi
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --debug "$src" -o "$D/prog" > "$D/compile.log" 2>&1 || { tail -5 "$D/compile.log"; exit 1; }
gdb -q -batch -ex "set pagination off" -ex run -ex "bt $frames" -ex 'x/2i $pc' -ex "info registers rax rcx rdx rdi rsi" --args "$D/prog" > "$D/gdb.log" 2>&1
grep -A $((frames + 12)) "SIGSEGV\|SIGABRT\|SIGBUS" "$D/gdb.log" | cut -c1-200
grep -q "SIG" "$D/gdb.log" || { echo "no crash:"; tail -3 "$D/gdb.log"; }
