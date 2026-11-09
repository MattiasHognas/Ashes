#!/bin/bash
# fulltally.sh <stage1-binary> <tag> : full exit census of loops10 and loops11 (every live RC cell,
# no row cutoff), per (size, first word) class, NET and GROWTH per function from 10 to 11.
# Each run dumps into its own directory (work/q40/run-<tag>-<pid>), so concurrent runs from any
# worktree never read each other's dumps; nothing goes through tinycensus.sh's shared $T/tiny-dump.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
Q=$R/work/q40
export LC_ALL=C
S1=$(realpath "$1"); tag=$2
W=$Q/run-$tag-$$
mkdir -p "$W"
base=$((0x100000000000)); chunk=$((256 * 1048576))
for n in 10 11; do
  D=$W/dump-$n; mkdir -p "$D"
  { echo "set pagination off"; echo "catch syscall exit exit_group"; echo "run"
    for k in 0 1; do echo "dump binary memory $D/full-$k.bin $((base + k * chunk)) $((base + (k + 1) * chunk))"; done
    echo "kill"; } > "$W/dump-$n.gdb"
  (cd "$R" && bash -c "ulimit -s 1048576; gdb -q -batch -x '$W/dump-$n.gdb' --args '$S1' compile '$R/artifacts/selfhost-dev/tiny/loops$n.ash' -o '$W/tiny-$n.out'") > "$W/gdb-$n.log" 2>&1
  for k in 0 1; do
    [ "$(stat -c %s "$D/full-$k.bin" 2>/dev/null || echo 0)" -eq "$chunk" ] || { echo "dump failed for loops$n (full-$k.bin missing or short)"; tail -5 "$W/gdb-$n.log"; exit 1; }
  done
  grep -q "Cannot access memory\|No such process\|exited normally" "$W/gdb-$n.log" && { echo "dump failed for loops$n (gdb did not stop at exit)"; tail -5 "$W/gdb-$n.log"; exit 1; }
  CELLS=1 "$Q/rccells" 0 0 "$D"/full-*.bin | awk '{print $3"_"$4}' | sort | uniq -c | awk '{print $2, $1}' | sort > "$W/tally-$n.txt"
done
join -a1 -a2 -e0 -o 0,1.2,2.2 "$W/tally-10.txt" "$W/tally-11.txt" | awk '{d=$3-$2; if (d!=0) print $1, $2, $3, d}' | sort -k4 -n -r > "$W/diff.txt"
cp "$W/diff.txt" "$Q/tally-$tag-diff.txt"
n10=$(awk '{s+=$2} END{print s}' "$W/tally-10.txt"); n11=$(awk '{s+=$2} END{print s}' "$W/tally-11.txt")
echo "$tag binary: $S1 sha1 $(sha1sum "$S1" | cut -c1-40) (run dir $W)"
echo "$tag NET: $((n11 - n10))/fn (live $n10 -> $n11)"
echo "$tag GROWTH: $(awk '$4>0{s+=$4} END{print s+0}' "$W/diff.txt")/fn (sum of positive class deltas)"
head -20 "$W/diff.txt"
