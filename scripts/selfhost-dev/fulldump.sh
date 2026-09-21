#!/bin/bash
# usage: fulldump.sh <stage1> <Module> <seconds> [GB] ; runs a one-import probe under gdb, interrupts it at
# <seconds> and dumps the RC region from its base in 256 MB chunks (full-<k>.bin, chunk k at base +
# k * 256 MB) up to [GB] (default 8), skipping chunks gdb cannot read.
. "$(dirname "$0")/env.sh"
bin=$1; module=$2; at=$3; gb=${4:-8}
printf 'import %s\nimport Ashes.IO as io\n\nio.print("probe")\n' "$module" > "$J/probe2/src/Main.ash"
rm -rf "$J/probe2/out" "$T"/full-*.bin
base=$((0x100000000000))
chunk=$((256 * 1048576))
{
    echo "set pagination off"
    echo "run"
    for k in $(seq 0 $((gb * 4 - 1))); do
        start=$((base + k * chunk))
        echo "dump binary memory $T/full-$k.bin $start $((start + chunk))"
    done
    echo "kill"
} > "$T/fulldump.gdb"
( sleep "$at"; pkill -INT -f "^$bin compile" ) &
systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 bash -c "ulimit -s 1048576; gdb -q -batch -x '$T/fulldump.gdb' --args '$bin' compile --project '$J/probe2/ashes.json'" > "$T/fulldump.log" 2>&1
ls "$T"/full-*.bin 2>/dev/null | wc -l
