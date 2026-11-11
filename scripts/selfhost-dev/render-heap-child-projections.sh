#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
fixture="$repo/selfhost/tests/heap-child-projections"
out="$repo/work/heap-child-projections"
for marker in 'let heapFactsOf ' 'let heapShallowCopyOf ' 'let heapChildrenOfNamed ' 'let inlineAdtCopyPlanOf '; do
    [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
mkdir -p "$out"
cat "$fixture/Prelude.ash.inc" > "$out/Main.ash"
awk '/^let heapFactsOf / {copying=1} copying && /^let heapShallowCopyOf / {exit} copying {print}' "$core" >> "$out/Main.ash"
awk '/^let heapChildrenOfNamed / {copying=1} copying && /^let inlineAdtCopyPlanOf / {exit} copying {print}' "$core" >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"heap-child-projections","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
