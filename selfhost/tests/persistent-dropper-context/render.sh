#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
out="$root/work/persistent-dropper-context"
mkdir -p "$out"
awk '
    !inserted && /export \(/ {
        sub(/export \(/, "export (\n    value generalRcAdmissible,")
        inserted = 1
    }
    { print }
    END { if (!inserted) exit 1 }
' "$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash" > "$out/ProbeDroppers.ash"
for name in StructuralDroppersTests DropperViewCacheTests DropperAdmissibilityProjectionTests; do
    sed '0,/import AshesCompiler\.Semantics\.StructuralDroppers/s//import ProbeDroppers/' \
        "$root/selfhost/tests/semantics/$name.ash" > "$out/$name.ash"
done
cp "$fixture/Main.ash" "$out/Main.ash"
printf '%s\n' '{"name":"persistent-dropper-context","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/dropper-view-check/ashes.lock" "$out/ashes.lock"
