#!/bin/bash
# usage: exitcensus.sh <stage1-leakcheck-binary> <outdir>; ashes-rc-live at exit for stage 1 compiling loops<N>.ash, per pair, with the per-function difference.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
H=/home/mattias/.claude/jobs/heavy2.sh
B=$1; S=$2
mkdir -p "$S"
date
live() {
    bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
        bash -c "ulimit -s 1048576; timeout 600 '$B' compile '$R/artifacts/selfhost-dev/tiny/loops$1.ash' -o '$S/loops$1.out'" > "$S/loops$1.log" 2>&1
    grep -a "ashes-rc-live" "$S/loops$1.log" | tail -1 | awk '{print $2}'
}
for pair in "10 11" "40 41" "80 81"; do
    set -- $pair
    a=$(live $1); b=$(live $2)
    echo "N=$1/$2 live $a/$b per-fn $(( b - a ))"
done
date
