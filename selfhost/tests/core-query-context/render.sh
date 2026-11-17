#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/core-query-context"
mkdir -p "$out"
core="$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
{
    cat "$fixture/Prelude.imports"
    for name in CoreHeapQueryFacts CoreHeapQueryContext emptyCoreHeapQueryFacts emptyCoreHeapQueryContext CoreConstructorLayout; do
        extract_declaration "$core" "$name"
    done
    cat "$fixture/State.ash"
    for name in stateCoverageTypes withStateCoverageTypes stateHeapQueryContext withStateHeapQueryContext coreHeapQueryFacts queryCoreCopyPlan queryCoreAdmissibility queryCoreShallowCopy queryCoreChildren queryCoreChildTypes stateDropperTypes withStateDropperTypes withStateDropperContext restoreHeapQueryContexts; do
        extract_declaration "$core" "$name"
    done
    cat "$fixture/ContextAdapter.ash"
    for name in admissibilityChildOf heapShallowCopyOfIndexed heapAdmissibilityOfIndexed heapCopyPlanFactsOfIndexed heapChildrenOfNamedIndexed heapChildTypesOfNamedIndexed ScopeCopyOut ArgumentCopyPlan GeneralRcAdmissibilityWork prependAdmissibilityTypes prependAdmissibilityChildren admissibilityVisitStatus originalGeneralRcAdmissibleIndexed searchGeneralRcAdmissibilityIndexed queryGeneralRcAdmissibilityIndexed isGeneralRcAdmissibleIndexed runtimeManagedCopyPlanLayout ownedCopyPlanChildren anyCopyPlanChildSurvivesNoReset copyPlanOfIndexed argumentCopyPlanOfIndexed generalCopyPlanOfIndexed anyChildSurvivesNoReset isGeneralRcNamedTypeIndexed reachesGeneralRcNamedTypeIndexed scopeCopyOutOfIndexed callCopyOutOfIndexed isGeneralRcValueTypeIndexed; do
        extract_declaration "$core" "$name"
    done
    cat "$fixture/Oracle.ash" "$fixture/Assertions.ash"
} > "$out/Main.ash"
printf '%s\n' '{"name":"core-query-context","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/dropper-view-check/ashes.lock" "$out/ashes.lock"
