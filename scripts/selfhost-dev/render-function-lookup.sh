#!/usr/bin/env bash
# Extract production lookup helpers for focused equivalence and lifetime checks.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
out="$repo/work/function-lookup-index"
[[ $(rg -F -c '// Prepared function lookup indexes:' "$core") == 1 ]]
[[ $(rg -F -c '// End prepared function lookup indexes.' "$core") == 1 ]]
mkdir -p "$out"
cat > "$out/Main.ash" <<'ASH'
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ReuseFunctionSpecialization.specializationArity
ASH
awk '/^\/\/ Prepared function lookup indexes:/ {copying=1} copying {print} /^\/\/ End prepared function lookup indexes\./ {exit}' "$core" >> "$out/Main.ash"
cat >> "$out/Main.ash" <<'ASH'
// Minimal state shell; the setters/accessors below are copied from production unchanged.
type CoreProgramState =
    | specializationCandidates: List((Str, List(Str), Expr))
    | specializationCandidateByName: MapTree(Str, CoreSpecializationCandidate)
    | topLevelFunctionRefs: List((Str, Str, TypeScheme))
    | topLevelFunctionRefByName: MapTree(Str, CoreTopLevelFunctionReference)
type CoreLoweringState =
    | programState: CoreProgramState
ASH
awk '/^let stateTopLevelFunctionRefIndex / {copying=1} copying && /^let stateInlinableHelpers / {exit} copying {print}' "$core" >> "$out/Main.ash"
awk '/^let stateSpecializationCandidates / {copying=1} copying && /^let stateGeneralRcOwnedResultLabels / {exit} copying {print}' "$core" >> "$out/Main.ash"
cat "$repo/selfhost/tests/function-lookup-index/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"function-lookup-index","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
printf 'Generated %s\n' "$out/ashes.json"
