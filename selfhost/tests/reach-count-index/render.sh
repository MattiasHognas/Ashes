#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/reach-count-index"
mkdir -p "$out"
path=selfhost/packages/semantics/src/AshesCompiler/Semantics/ResultReachSummaries.ash
old="$out/ReferenceResultReachSummaries.ash.inc"
git -C "$root" show "beaa9a30:$path" > "$old"
prefix_module_exports "$old" Ref ref_ > "$out/ReferenceResultReachSummaries.ash"
old_names=(lookupCount replaceCount holdsCount setCount capped lookupCountOr namesRepeat sumCountsEntryByEntry summedOntoLeft summedBesideLeft sumCounts maxCountsEntryByEntry maximizedOntoLeft absentFromLeft maxCounts)
{
    cat "$fixture/CountPrelude.ash"
    for name in lookupCount replaceCount holdsCount setCount capped lookupCountOr linear_namesRepeat linear_summedOntoLeft linear_summedBesideLeft linear_maximizedOntoLeft linear_absentFromLeft hasCountIndexSize indexReachCounts lookupIndexedCountOr namesRepeatIndexed namesRepeat sumCountsEntryByEntry summedOntoLeft summedBesideLeft sumCounts maxCountsEntryByEntry maximizedOntoLeft absentFromLeft maxCounts; do
        printf '\n'
        extract_declaration "$root/$path" "$name"
    done
    printf '\n'
    {
        separator=''
        for name in "${old_names[@]}"; do
            printf '%s' "$separator"
            extract_declaration "$old" "$name"
            separator=$'\n'
        done
    } | rename_identifiers <(for name in "${old_names[@]}"; do printf '%s old_%s\n' "$name" "$name"; done)
    printf '\n'
    cat "$fixture/CountAdapters.ash"
} > "$out/CountQueries.ash"
cp "$fixture/Main.ash" "$out/Main.ash"
printf '%s' '{"name":"reach-count-index","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/type-graph-path-cut/ashes.lock" "$out/ashes.lock"
printf '%s\n' "$out/ashes.json"
