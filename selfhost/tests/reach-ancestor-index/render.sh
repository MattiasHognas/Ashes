#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/reach-ancestor-index"
mkdir -p "$out"
path=selfhost/packages/semantics/src/AshesCompiler/Semantics/ResultReachSummaries.ash
old="$out/ReferenceResultReachSummaries.ash.inc"
git -C "$root" show "ac493aa2:$path" > "$old"
prefix_module_exports "$old" Ref ref_ > "$out/ReferenceResultReachSummaries.ash"
{
    cat "$fixture/PairPrelude.ash"
    for name in indexReachNames hasPathSuffix hasIndexedPathAncestor anyIndexedPathAncestor hasPathAncestorPair; do
        printf '\n'
        extract_declaration "$root/$path" "$name"
    done
    printf '\n'
    cat "$fixture/IndexedAdapter.ash"
    printf '\n'
    {
        extract_declaration "$old" hasDescendantOf
        printf '\n'
        extract_declaration "$old" hasPathAncestorPair
    } | rename_identifiers <(printf '%s\n' 'hasDescendantOf oldHasDescendantOf' 'hasPathAncestorPair oldHasPathAncestorPair')
    printf '\n'
    cat "$fixture/ReferenceAdapter.ash"
} > "$out/PairQueries.ash"
cp "$fixture/Main.ash" "$out/Main.ash"
printf '%s' '{"name":"reach-ancestor-index","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/type-graph-path-cut/ashes.lock" "$out/ashes.lock"
printf '%s\n' "$out/ashes.json"
