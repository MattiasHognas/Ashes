#!/bin/bash
# usage: tinyrootshape.sh <len> <elem> [tag] ; over the dump the last tinycensus.sh left, the leaked list-head
# roots of one shape (list length, element cell size, optionally the element's first word), tallied by the first
# string reachable from the head element, and a few example addresses: what those lists hold.
. "$(dirname "$0")/env.sh"
len=$1; elem=$2; tag=${3:-}
census_tool rcroots || exit 1
ir_kinds_table
"$T/rcroots" "$T/tiny-dump" "$T/irkinds.txt" 10000000 > "$T/rootshape.all"
awk -v l="$len" -v e="$elem" -v t="$tag" '$2 == "len=" l || $2 == "len="l { }
    { split($2, a, "="); split($3, b, "="); split($4, c, "=");
      if (a[2] == l && b[2] == e && (t == "" || c[2] == t)) { text = ""; for (i = 5; i <= NF; i++) text = text " " $i; print $1, text } }' \
    "$T/rootshape.all" > "$T/rootshape.sel"
echo "roots of this shape: $(wc -l < "$T/rootshape.sel")"
cut -d' ' -f2- "$T/rootshape.sel" | sort | uniq -c | sort -rn | head -${LINES_SHOWN:-25}
echo "examples: $(head -3 "$T/rootshape.sel" | cut -d' ' -f1 | tr '\n' ' ')"
