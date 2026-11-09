#!/bin/bash
# usage: peekdeep.sh <addr-file> ; for each payload address in the last probe core: the cell, the cells its first two
# words point at, and the cell the first of those points at, with any string they hold. One gdb run per address, so
# a word that is not a pointer stops only its own sample.
W=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev
G=$W/handoff-step-e/work/pd.gdb
while read -r a; do
  {
    echo "echo === $a\\n"
    echo "x/6gx 0x$a-16"
    echo "set \$p0 = *(long*)0x$a"
    echo "set \$p1 = *(long*)(0x$a+8)"
    echo "echo -- first\\n"
    echo "x/6gx \$p0-16"
    echo "x/s \$p0+8"
    echo "echo -- second\\n"
    echo "x/6gx \$p1-16"
    echo "x/s \$p1+8"
    echo "set \$q0 = *(long*)\$p0"
    echo "echo -- first.first\\n"
    echo "x/6gx \$q0-16"
    echo "x/s \$q0+8"
  } > "$G"
  gdb -q -batch -x "$G" "$W/s1-table" "$W/logs/probe.core" 2>&1 | grep -v "^warning\|debuginfod\|Debuginfod\|^  <http\|^Enable\|^This GDB\|^To make\|^Core was\|^Program term\|^#0\|New LWP\|^$" | cut -c1-160
done < "$1"
