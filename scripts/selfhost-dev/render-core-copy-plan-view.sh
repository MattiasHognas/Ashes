#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fixture="$repo/selfhost/tests/core-copy-plan-view"
out="$repo/work/core-copy-plan-view"
mkdir -p "$out"
source "$repo/selfhost/tests/render-lib.sh"
core=$(core_lowering_source "$out/core-lowering-source.txt")
for marker in 'let heapFactsOf ' 'let heapShallowCopyOf ' 'let heapCopyPlanFactsOf ' 'let argumentCopyPlanOf ' 'let heapChildrenOfNamed ' 'let heapChildTypesOfNamed ' 'and emitNormalizerAdtCopy ' 'let emitArgumentCopyByCopy '; do
 [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
cat "$fixture/Prelude.ash.inc" > "$out/Main.ash"
extract_range "$core" '^let heapFactsOf ' '^let heapShallowCopyOf ' >> "$out/Main.ash"
cat "$fixture/OldPlan.ash.inc" >> "$out/Main.ash"
extract_range "$core" '^let heapCopyPlanFactsOf ' '^let argumentCopyPlanOf ' >> "$out/Main.ash"
extract_range "$core" '^let heapChildrenOfNamed ' '^let heapChildTypesOfNamed ' >> "$out/Main.ash"
cat "$fixture/Normalizer.ash.inc" >> "$out/Main.ash"
# This member of a production recursive group has no recursive calls of its own.
# Change only its declaration keyword so the exact caller body compiles in isolation.
extract_range "$core" '^and emitNormalizerAdtCopy ' '^let emitArgumentCopyByCopy ' | sed '1s/^and /let /' >> "$out/Main.ash"
cat "$fixture/NormalizerAssertions.ash.inc" >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"core-copy-plan-view","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
