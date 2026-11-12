#!/bin/bash
# Render an independent historical oracle beside the current production driver.
set -euo pipefail
repo=$(git rev-parse --show-toplevel)
out="$repo/work/ownership-lookup-regression"
mkdir -p "$out"
git show e34238b6:selfhost/packages/semantics/src/AshesCompiler/Semantics/OwnershipInference.ash | sed -e 's/\bFunctionSignature\b/BaselineFunctionSignature/g' -e 's/\bProgramParameterOwnership\b/BaselineProgramParameterOwnership/g' -e 's/\bMoveCallSite\b/BaselineMoveCallSite/g' > "$out/Baseline.ash"
cp "$repo/selfhost/tests/ownership-lookup/Main.ash" "$out/Main.ash"
cp "$repo/selfhost/tests/ownership-lookup/PrebuiltFixture.ash" "$out/PrebuiltFixture.ash"
cp "$repo/selfhost/tests/ownership-lookup/PrebuiltMain.ash" "$out/PrebuiltMain.ash"
cp "$repo/selfhost/tests/type-graph-path-cut/ashes.lock" "$out/ashes.lock"
cat > "$out/ashes.json" <<'JSON'
{"name":"ownership-lookup-regression","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
if [ "${1:-}" = prebuilt ]; then
    cp "$repo/selfhost/tests/ownership-lookup/PrebuiltMain.ash" "$out/Main.ash"
elif [ "${1:-}" = wrapped ]; then
    cp "$repo/selfhost/tests/ownership-lookup/WrappedMain.ash" "$out/Main.ash"
fi
printf '%s\n' "$out/ashes.json"
