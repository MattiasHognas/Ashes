#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
fixture="$repo/selfhost/tests/core-copy-plan-view"
out="$repo/work/core-copy-plan-view"
for marker in 'let heapFactsOf ' 'let heapShallowCopyOf ' 'let heapCopyPlanFactsOf ' 'let argumentCopyPlanOf '; do
 [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
mkdir -p "$out"
cat "$fixture/Prelude.ash.inc" > "$out/Main.ash"
awk '/^let heapFactsOf / {copying=1} copying && /^let heapShallowCopyOf / {exit} copying {print}' "$core" >> "$out/Main.ash"
cat "$fixture/OldPlan.ash.inc" >> "$out/Main.ash"
awk '/^let heapCopyPlanFactsOf / {copying=1} copying && /^let argumentCopyPlanOf / {exit} copying {print}' "$core" >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"core-copy-plan-view","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
