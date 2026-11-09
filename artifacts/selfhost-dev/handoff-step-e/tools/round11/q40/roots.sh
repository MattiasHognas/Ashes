#!/bin/bash
# roots32.sh <s1-tag> <n> <size> : unreferenced SIZE-byte roots at exit of loops<n>, tallied by the shape of their two fields (depth 2)
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
cd "$R" || exit 1
. scripts/selfhost-dev/env.sh
bash scripts/selfhost-dev/tinycensus.sh artifacts/selfhost-dev/s1-$1 artifacts/selfhost-dev/tiny/loops$2.ash > /dev/null 2>&1
"$T/rcrootsof" "$T/tiny-dump" $3 100000 2>/dev/null > work/q40/r$3-$1-$2.txt
xargs -a work/q40/r$3-$1-$2.txt "$T/rcpeek" "$T/tiny-dump" 2 2>/dev/null | awk '
/^cell /{ if (sig != "") print sig; sig="ROOT"; next }
/^    [^ ]/ { s=$0; sub(/^ +/, "", s); sub(/cell [0-9a-f]+ /, "cell ", s); sub(/-> [0-9a-f]+/, "->", s); sig=sig " | " s; next }
/^        [^ ]/ { s=$0; sub(/^ +/, "", s); sub(/cell [0-9a-f]+ /, "cell ", s); sub(/-> [0-9a-f]+/, "->", s); sig=sig " / " s; next }
END { if (sig != "") print sig }' | sed -E 's/"[a-z_]*[0-9]+[a-z_0-9]*"/"<numbered>"/g' | sort | uniq -c | sort -rn > work/q40/rsig$3-$1-$2.txt
wc -l < work/q40/r$3-$1-$2.txt
