#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
path=selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash
for flavor in baseline candidate; do
    out="$root/work/persistent-dropper-$flavor"
    mkdir -p "$out"
    if [[ $flavor == baseline ]]; then
        git -C "$root" show "5804df05:$path" > "$out/ProbeDroppers.ash"
    else
        cp "$root/$path" "$out/ProbeDroppers.ash"
    fi
    awk -v adapter="$fixture/Carry-$flavor.ash" '
        FILENAME == adapter { replacement = replacement $0; next }
        { sub(/CONTEXT_CARRY/, replacement); print }
    ' "$fixture/Carry-$flavor.ash" "$fixture/Synthesis.ash" > "$out/Main.ash"
    printf '{"name":"persistent-dropper-%s","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n' "$flavor" > "$out/ashes.json"
    cp "$root/selfhost/tests/dropper-view-check/ashes.lock" "$out/ashes.lock"
done
