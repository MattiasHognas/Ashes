#!/bin/bash
# usage: probepeak.sh <stage1-binary>... ; runs the IrCodegen module probe to completion with each stage 1 under
# the 40G cap and prints wall time, peak RSS, exit status and the last output line.
. "$(dirname "$0")/env.sh"
printf 'import AshesCompiler.Backend.IrCodegen\nimport Ashes.IO as io\n\nio.print("probe")\n' > "$J/probe2/src/Main.ash"
for bin in "$@"; do
    rm -rf "$J/probe2/out"
    systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 \
        /usr/bin/time -f '%e %M' -o "$J/probepeak.time" bash -c "ulimit -s 1048576; '$bin' compile --project '$J/probe2/ashes.json'" > "$J/probepeak.log" 2>&1
    code=$?
    read -r secs kb < <(tail -1 "$J/probepeak.time")
    printf '%-16s exit=%-3s %7ss %8s MB  last=%s\n' "$(basename "$bin")" "$code" "$secs" "$(awk -v k="${kb:-0}" 'BEGIN{printf "%.0f", k/1024}')" "$(tail -1 "$J/probepeak.log" | cut -c1-70)"
done
