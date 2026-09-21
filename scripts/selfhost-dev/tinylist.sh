#!/bin/bash
# usage: tinylist.sh <stage1-binary> <src.ash> [last-n] ; lists the leaked states at exit (address order),
# the last n of them (default 25): the most recently allocated belong to the last things lowered.
. "$(dirname "$0")/env.sh"
census_tool rcstates && ir_kinds_table || exit 1
CENSUS_LINES=1 STATE_LINES=1 bash "$HERE/tinycensus.sh" "$1" "$2" > /dev/null
RCSTATES_LIST=1 "$T/rcstates" "$T/tiny-dump" "$T/irkinds.txt" | grep "^state" > "$T/tinylist.txt"
echo "leaked states: $(wc -l < "$T/tinylist.txt")"
tail -${3:-25} "$T/tinylist.txt"
