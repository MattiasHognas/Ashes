#!/bin/bash
# usage: l8count.sh <s1-binary> <program.ash> <outdir> [string-hex] : live 32-byte RC cells at stage 1's exit, total and those holding the given 8-byte string (default "lambda_8")
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
B=$(realpath "$1"); P=$(realpath "$2"); W=$3; H=${4:-6c616d6264615f38}
mkdir -p "$W"; base=$((0x100000000000)); chunk=$((256*1048576))
{ echo "set pagination off"; echo "set debuginfod enabled off"; echo "catch syscall exit exit_group"; echo "run compile $P -o $W/t.out"; for k in 0 1; do echo "dump binary memory $W/full-$k.bin $((base + k*chunk)) $((base + (k+1)*chunk))"; done; echo kill; } > "$W/d.gdb"
"$R/work/cp/cap.sh" 8G bash -c "ulimit -s 1048576; ASHES_RC_LEAKCHECK=1 gdb -q -batch -x $W/d.gdb $B" > "$W/gdb.log" 2>&1
CELLS=1 "$R/work/q40/rccells" 0 0 "$W"/full-*.bin > "$W/cells.txt"
n=0; for a in $(awk '$3==32 && $4==8 {print $2}' "$W/cells.txt"); do off=$((0x$a - base)); f=$W/full-0.bin; [ $off -ge $chunk ] && { off=$((off-chunk)); f=$W/full-1.bin; }; [ "$(od -A n -t x1 -j $((off+24)) -N 8 $f | tr -d ' \n')" = "$H" ] && n=$((n+1)); done
echo "$(basename $2): live=$(wc -l < $W/cells.txt) l8=$n"
