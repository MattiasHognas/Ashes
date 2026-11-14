#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/handoff-ownership-index"
mkdir -p "$out"
core="$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
ownership="$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/OwnershipInference.ash"
git -C "$root" show ce3fbd27:selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash > "$out/baseline-source.txt"
{
    cat "$fixture/Prelude.ash"
    extract_declaration "$ownership" lookupProgramParameterOwnership
    for name in overlayProvenBorrows sameParameterNames registeredTopLevelName provenParameterOwnership; do
        extract_declaration "$core" "$name"
    done
    extract_declaration "$out/baseline-source.txt" provenParameterOwnership |
        sed -e 's/\<provenParameterOwnership\>/oldProvenParameterOwnership/g' \
            -e 's/stateProgramParameterOwnership/(given (state: CoreLoweringState) -> state.oldTable)/g'
    cat "$fixture/Assertions.ash"
} | sed 's/(table: ProgramParameterOwnership)/(table: List((Str, List((Str, ParameterOwnership)))))/g' > "$out/Main.ash"
printf '%s' '{"name":"handoff-ownership-index","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/type-graph-path-cut/ashes.lock" "$out/ashes.lock"
