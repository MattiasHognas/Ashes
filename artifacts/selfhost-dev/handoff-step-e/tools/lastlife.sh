#!/bin/bash
# usage: lastlife.sh [frames] ; the events of the last tinywatch.sh run after the cell's last allocation (PTR -> 1)
L=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/logs/tinywatch.log
frames=${1:-4}
awk -v nf="$frames" '
/^Old value/ { old=$4 }
/^New value/ { if (line != "") print line; nv=$4; n=0; line=sprintf("%s -> %s :", (length(old) > 6 ? "PTR" : old), (length(nv) > 6 ? "PTR" : nv)) }
/^#[0-9]+ / {
    n++; if (n > nf) next;
    fn=$2; if ($2 ~ /^0x/) fn=$4;
    ln=$NF; sub(/.*\//, "", ln);
    line=line " " fn "@" ln
}
END { if (line != "") print line }
' "$L" | sed 's/CoreLowering.ash://g' | awk '/^PTR -> 1/ { buf="" } { buf = buf $0 "\n" } END { printf "%s", buf }'
