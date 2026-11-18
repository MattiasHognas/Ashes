#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fixture="$repo/selfhost/tests/heap-child-projections"
out="$repo/work/heap-child-projections"
mkdir -p "$out"
source "$repo/selfhost/tests/render-lib.sh"
core=$(core_lowering_source "$out/core-lowering-source.txt")
for marker in 'let heapFactsOf ' 'let heapShallowCopyOf ' 'let heapChildrenOfNamed ' 'let inlineAdtCopyPlanOf '; do
    [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
cat "$fixture/Prelude.ash.inc" > "$out/Main.ash"
extract_range "$core" '^let heapFactsOf ' '^let heapShallowCopyOf ' >> "$out/Main.ash"
extract_declaration "$core" cachedCoreChildren >> "$out/Main.ash"
extract_range "$core" '^let heapChildrenOfNamed ' '^let inlineAdtCopyPlanOf ' >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"heap-child-projections","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
