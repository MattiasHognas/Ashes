#!/bin/bash
# usage: tinynamedroots.sh <stage1> <src.ash> <size> [pattern] ; takes the exit census of a small input and lists
# the leaked roots of one cell size whose first field is a string, with that string: records such as a syntax
# tree's let binding are recognised by the name they carry. With a pattern, only the matching names.
. "$(dirname "$0")/env.sh"
bin=$1; src=$2; size=$3; pat=${4:-.}
bash "$HERE/tinycensus.sh" "$bin" "$src" > /dev/null 2>&1
census_tool rcrootsof && census_tool rcpeek || exit 1
"$T/rcrootsof" "$T/tiny-dump" "$size" 100000 2> /dev/null > "$T/namedroots.addrs"
echo "roots of size $size: $(wc -l < "$T/namedroots.addrs")"
xargs -a "$T/namedroots.addrs" "$T/rcpeek" "$T/tiny-dump" 1 2> /dev/null \
    | awk '/^cell /{ cell = $2; first = 1; next } /^    cell /{ first = 0 } first { first = 0; if ($0 ~ /^    "/) print cell, $0 }' \
    | grep -E "$pat" | tail -${LINES_SHOWN:-12} | cut -c1-140
