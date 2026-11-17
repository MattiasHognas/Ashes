#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
source selfhost/tests/render-lib.sh
out=work/logical-not-lifetime
fixture=selfhost/tests/logical-not-inference
core=selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash
mkdir -p "$out"
{
    cat "$fixture/LifetimePrelude.ash"
    for name in clearAdmissibilityAnswers withSubstitution resolveType failure bindType statePendingTraitGoals withStatePendingTraitGoals stateProvenTraitGoals withStateProvenTraitGoals stateTraitGoalsPinned withStateTraitGoalsPinned traitGoalKey pinProvenTraitGoal unprovenTraitGoalKey deferTraitGoal reserveCoreTemps finishCoreLogicalNot finishOrDeferCoreLogicalNot resolvedTraitGoals traitGoalMap; do
        extract_declaration "$core" "$name"
    done
    cat "$fixture/LifetimeAssertions.ash"
} > "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"logical-not-lifetime","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp selfhost/tests/semantics/ashes.lock "$out/ashes.lock"
