#!/bin/bash
# usage: probecore.sh <stage1> <seconds> [project.json] ; runs stage 1 on a project (default: the module probe)
# under gdb, interrupts it at <seconds>, and writes its memory mappings and a core file of everything readable:
# the input of reachcensus. The core is $T/probe.core, the mappings $T/probe.maps.
. "$(dirname "$0")/env.sh"
bin=$(readlink -f "$1"); at=$2; project=${3:-$J/probe2/ashes.json}
if [ -z "$3" ]; then
    printf 'import AshesCompiler.Backend.IrCodegen\nimport Ashes.IO as io\n\nio.print("probe")\n' > "$J/probe2/src/Main.ash"
    rm -rf "$J/probe2/out"
fi
rm -f "$T/probe.core" "$T/probe.maps"
{
    echo "set pagination off"
    echo "set use-coredump-filter off"
    echo "run"
    echo "info proc mappings"
    echo "generate-core-file $T/probe.core"
    echo "kill"
} > "$T/probecore.gdb"
( sleep "$at"; pkill -INT -f "^$bin compile" ) &
systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 bash -c "ulimit -s 1048576; gdb -q -batch -x '$T/probecore.gdb' --args '$bin' compile --project '$project'" > "$T/probecore.log" 2>&1
grep -E "^ *0x[0-9a-f]+ +0x" "$T/probecore.log" > "$T/probe.maps"
echo "mappings: $(wc -l < "$T/probe.maps")  core: $(du -h "$T/probe.core" 2>/dev/null | cut -f1)"
grep -i "saved corefile\|warning: Memory read failed\|error" "$T/probecore.log" | sort | uniq -c | head -4
