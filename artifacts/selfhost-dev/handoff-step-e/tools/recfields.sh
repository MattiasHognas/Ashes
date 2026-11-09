#!/bin/bash
# usage: recfields.sh <count> ; stage-1 record types with exactly <count> fields (declaration order)
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
find "$R/selfhost/packages" "$R/lib" -name '*.ash' | while read -r f; do
  awk -v want="$1" -v file="$f" '
    function flush() { if (name != "" && n == want) { printf "%s %s:%s\n", name, file, line; print fields } name=""; n=0; fields="" }
    /^type [A-Za-z_]+/ { flush(); name=$2; line=NR; next }
    name != "" && /^    \| [a-z][A-Za-z0-9_]*:/ { n++; f=$2; fields = fields "  " n ":" f " " $3 "\n"; next }
    name != "" && /^ *\/\// { next }
    name != "" && !/^    \|/ { flush() }
    END { flush() }' "$f"
done
