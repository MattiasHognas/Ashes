#!/bin/bash
# cells.sh <binary> <outdir> : live RC cells at exit, one "size_firstword count" line per class
export LC_ALL=C
B=$(realpath "$1"); D=$2; mkdir -p "$D"
base=$((0x100000000000)); chunk=$((256 * 1048576))
{ echo "set pagination off"; echo "catch syscall exit exit_group"; echo "run"
  for k in 0 1; do echo "dump binary memory $D/full-$k.bin $((base + k * chunk)) $((base + (k + 1) * chunk))"; done
  echo "kill"; } > "$D/d.gdb"
gdb -q -batch -x "$D/d.gdb" --args "$B" > "$D/gdb.log" 2>&1
CELLS=1 /home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture/work/q40/rccells 0 0 "$D"/full-*.bin > "$D/cells.txt"
awk '{print $3"_"$4}' "$D/cells.txt" | sort | uniq -c | awk '{print $2, $1}' | sort > "$D/tally.txt"
