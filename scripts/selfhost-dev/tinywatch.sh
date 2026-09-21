#!/bin/bash
# usage: tinywatch.sh <stage1-debug-binary> <src.ash> <cell-hex> [stops] [frames] [tail-events]
# Every write to one cell's reference-count word while stage 1 compiles a small file, one line per event:
# old -> new, then the top frames as function:line. The run is deterministic under gdb, so an address
# from tinylist.sh on the same binary and input is the same cell. An address is recycled by the free
# list, so only the events after the last allocation (new value 1 from a pointer-sized old value)
# describe the leaked incarnation; the last <tail-events> events are printed.
. "$(dirname "$0")/env.sh"
bin=$1; src=$2; cell=$3; stops=${4:-80}; frames=${5:-5}; tailn=${6:-14}
{
    echo "set pagination off"
    echo "set confirm off"
    echo "watch *(long *)0x$cell"
    echo "run"
    echo "bt $frames"
    for _ in $(seq 1 "$stops"); do
        echo "continue"
        echo "bt $frames"
    done
    echo "kill"
} > "$T/tinywatch.gdb"
bash -c "ulimit -s 1048576; timeout 900 gdb -q -batch -x '$T/tinywatch.gdb' --args '$bin' compile '$src' -o '$J/tiny.out'" > "$T/tinywatch.log" 2>&1
awk '
/^Old value/ { old=$4 }
/^New value/ { if (line != "") print line; nv=$4; line=sprintf("%s -> %s :", (length(old) > 6 ? "PTR" : old), (length(nv) > 6 ? "PTR" : nv)) }
/^#[0-9]+ / {
    fn=$2; if ($2 ~ /^0x/) fn=$4;
    ln=$NF; sub(/.*\//, "", ln);
    line=line " " fn "@" ln
}
END { if (line != "") print line }
' "$T/tinywatch.log" | sed 's/CoreLowering.ash://g' | tail -"$tailn"
grep -c "^Old value" "$T/tinywatch.log" | sed 's/^/events: /'
grep -E "exited|The program is not being run" "$T/tinywatch.log" | head -2
