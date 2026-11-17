#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/generic-tuple-byte-parts-mirror"
mkdir -p "$out"
core="$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
{
    cat "$fixture/MirrorPrelude.ash"
    for name in nthElementType definitionOfTemp storedSourcesOf readOutOfOwnedValue isFreshRuntimeTemp isTuplePartOffset emitJumpUnlessTuplePartsRequested borrowedTuplePartSource tuplePartStoreCount borrowedStringDemotion stringPartStoreInterval stringPartIntervalHasLabel stringPartIntervalKeepsOwner borrowedStringSurvivesUntilStore guardStringDemotion preserveRequestedStringDemotion preserveRequestedBorrowedStringParts tuplePartStores; do
        extract_declaration "$core" "$name"
    done
    cat "$fixture/MirrorAssertions.ash"
} > "$out/Main.ash"
printf '%s\n' '{"name":"generic-tuple-byte-parts-mirror","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/semantics/ashes.lock" "$out/ashes.lock"
