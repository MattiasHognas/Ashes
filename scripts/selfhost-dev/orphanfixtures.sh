#!/bin/bash
# Lists the lowered-IR fixtures that the C# parity test does not regenerate, and for each whether the current
# stage 0 (the worktree's built CLI) still produces the stored IR (locations stripped, trait evidence dropped).
. "$(dirname "$0")/env.sh"
D=$R/selfhost/parity/semantics/lowered-ir
cd "$R" || exit 1
mkdir -p "$J/orphans"
strip() { sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$1" | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip'; }
for src in "$D"/*.source; do
    name=$(basename "$src" .source)
    grep -q "\"$name\"" src/Ashes.Tests/SelfhostIrParityTests.cs && continue
    cp "$src" "$J/orphans/$name.ash"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$J/orphans/$name.ash" -o "$J/orphans/bin" --emit-ir lowered > /dev/null 2> "$J/orphans/$name.raw"
    strip "$J/orphans/$name.raw" > "$J/orphans/$name.now"
    strip "$D/$name.ir" > "$J/orphans/$name.stored"
    printf '%-52s stage0-now-vs-stored: %s lines differ\n' "$name" "$(diff "$J/orphans/$name.stored" "$J/orphans/$name.now" | grep -c '^[<>]')"
done
