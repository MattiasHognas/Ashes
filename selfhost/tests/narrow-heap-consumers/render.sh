#!/usr/bin/env bash
# Render exact candidate query boundaries beside an immutable historical classifier.
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/narrow-consumers"
mkdir -p "$out"
source_path=selfhost/packages/semantics/src/AshesCompiler/Semantics
reference="$out/ReferenceHeapLayoutClassification.ash.inc"
git -C "$root" show "b19daa03:$source_path/HeapLayoutClassification.ash" > "$reference"
prefix_module_exports "$reference" Ref ref_ > "$out/ReferenceHeapLayoutClassification.ash"
git -C "$root" show "b19daa03:$source_path/CoreLowering.ash" > "$out/baseline-core.txt"
extract_declaration "$out/baseline-core.txt" canNormalizeClosureCapture |
    sed -e 's/let canNormalizeClosureCapture/let oldCanNormalizeClosureCapture/g' \
        -e 's/recordAdtSupported(heapFactsOf(named)(state))/oldFact(named)(state)(0)/g' \
        -e 's/ownedChildAdtSupported(heapFactsOf(named)(state))/oldFact(named)(state)(1)/g' > "$out/old-capture.txt"
{
    cat "$fixture/Prelude.ash"
    printf '\n'
    cat "$fixture/Environment.ash"
    for name in scalarCaptureText classifyClosureCaptureLayout heapClosureCaptureLayoutOf canNormalizeClosureCapture ownedChildrenDroppable; do
        printf '\n'
        extract_declaration "$root/$source_path/CoreLowering.ash" "$name"
    done
    printf '\n'
    awk -v capture="$out/old-capture.txt" '
        /^let agrees / && !inserted {
            while ((getline line < capture) > 0) print line
            close(capture)
            print ""
            inserted = 1
        }
        { print }
        END { if (!inserted) exit 1 }
    ' "$fixture/Assertions.ash"
} > "$out/Main.ash"
printf '%s' '{"name":"narrow-consumers","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/type-graph-path-cut/ashes.lock" "$out/ashes.lock"
printf '%s\n' "$out/ashes.json"
