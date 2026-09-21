#!/bin/bash
# usage: biggraph.sh <stage1> <seconds> <GB> [lines] ; snapshots the code generator module probe at <seconds> and
# prints what holds the reference-counted heap at that moment: the leaked-state summary, then the root classes with
# exclusive attribution, largest first.
. "$(dirname "$0")/env.sh"
bin=$(readlink -f "$1"); secs=$2; gb=$3; lines=${4:-24}
bash "$HERE/bigwatch.sh" dump "$bin" "$secs" "$gb" | head -12
census_tool rcgraph2 || exit 1
"$T/rcgraph2" "$T/big-dump" 2> /dev/null > "$T/biggraph.txt"
head -"$lines" "$T/biggraph.txt" | cut -c1-170
