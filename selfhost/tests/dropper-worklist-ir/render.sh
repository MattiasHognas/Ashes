#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
candidate=${1:-selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash}
for flavor in baseline candidate; do
    out="work/dropper-worklist-ir-$flavor"
    mkdir -p "$out"
    if [[ $flavor == baseline ]]; then
        cp selfhost/tests/dropper-worklist-ir/FrozenStructuralDroppers.ash "$out/ProbeDroppers.ash"
    else
        cp "$candidate" "$out/ProbeDroppers.ash"
    fi
    for name in StructuralCopiers ToSpaceCopiers; do
        sed 's/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/g' "selfhost/packages/semantics/src/AshesCompiler/Semantics/$name.ash" > "$out/Probe$name.ash"
    done
    for name in StructuralDroppersTests DropperViewCacheTests DropperAdmissibilityProjectionTests; do
        sed 's/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/g' "selfhost/tests/semantics/$name.ash" > "$out/$name.ash"
    done
    sed -e 's/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/g' -e 's/import AshesCompiler\.Semantics\.StructuralCopiers/import ProbeStructuralCopiers/g' -e 's/import AshesCompiler\.Semantics\.ToSpaceCopiers/import ProbeToSpaceCopiers/g' selfhost/tests/dropper-view-check/CopierFixture.ash > "$out/CopierFixture.ash"
    cp selfhost/tests/dropper-worklist-ir/Main.ash "$out/Main.ash"
    printf '{"name":"dropper-worklist-ir-%s","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n' "$flavor" > "$out/ashes.json"
    cp selfhost/tests/dropper-view-check/ashes.lock "$out/ashes.lock"
    sha256sum "$out/ProbeDroppers.ash" | cut -d ' ' -f 1 > "$out/dropper-sha256.txt"
done
