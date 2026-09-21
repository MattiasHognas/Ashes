#!/bin/bash
# usage: tinydiff.sh <stage1-binary> <src.ash>... ; the leaked-state census at exit for each small input
. "$(dirname "$0")/env.sh"
bin=$1; shift
for src in "$@"; do
    echo "== $(basename "$src"): $(tr '\n' ';' < "$src" | cut -c1-150)"
    CENSUS_LINES=2 STATE_LINES=${STATE_LINES:-12} bash "$HERE/tinycensus.sh" "$bin" "$src" | sed 's/^/   /'
done
