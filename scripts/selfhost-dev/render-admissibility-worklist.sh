#!/usr/bin/env bash
# Generate a diagnostic driver from current production traversal code, without building/running.
# usage: render-admissibility-worklist.sh [candidate core lowering source]
# The optional argument is a core lowering text to extract from instead of this checkout's (for example one
# written by `core_lowering_source` in another worktree).
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
droppers="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash"
fixture="$repo/selfhost/tests/admissibility-worklist"
out="$repo/work/admissibility-worklist"
mkdir -p "$out"
source "$repo/selfhost/tests/render-lib.sh"
if [ $# -ge 1 ]; then core=$1; else core=$(core_lowering_source "$out/core-lowering-source.txt"); fi
reference_commit=cc50f0f52b4ee76ffa0f96ee3c70e288bd273abd
# Fail on renamed or ambiguous extraction boundaries rather than silently testing stale code.
for marker in 'type GeneralRcAdmissibilityWork =' 'let isGeneralRcAdmissibleIndexed (semanticType:'; do
    [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
[[ $(rg -F -c 'let sameTypeKey ' "$droppers") == 1 ]]
[[ $(awk '/^\/\/ Stage 0.*IsGeneralRcAdmissible/ { count++ } END { print count+0 }' "$droppers") == 1 ]]
cat "$fixture/FixturePrelude.ash.inc" > "$out/Main.ash"
awk '/^let sameTypeKey / { copying=1 } copying && /^\/\/ Stage 0.*IsGeneralRcAdmissible/ { exit } copying { print }' "$droppers" >> "$out/Main.ash"
extract_range "$core" '^type GeneralRcAdmissibilityWork =' '^let isGeneralRcAdmissibleIndexed [(]semanticType:' > "$out/candidate-extracted.ash.inc"
cp "$fixture/Reference.ash.inc" "$out/reference-extracted.ash.inc"
cat "$fixture/Reference.ash.inc" "$out/candidate-extracted.ash.inc" "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
printf '%s:selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash\ntracked reference sha256 %s\n' \
    "$reference_commit" "$(sha256sum < "$fixture/Reference.ash.inc" | cut -d' ' -f1)" > "$out/reference-source.txt"
printf '%s\nsha256 %s\n' "$(realpath "$core")" "$(sha256sum < "$core" | cut -d' ' -f1)" > "$out/candidate-source.txt"
cat > "$out/ashes.json" <<'JSON'
{
  "name": "ashes-admissibility-worklist",
  "entry": "Main.ash",
  "sourceRoots": ["."],
  "outDir": "out",
  "devDependencies": {
    "semantics": { "path": "../../selfhost/packages/semantics" }
  },
  "overrides": {
    "AshesCompiler.Frontend": { "path": "../../selfhost/packages/frontend" }
  }
}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$droppers" "$out/Main.ash" > "$out/source-hashes.txt"
printf 'Generated %s\n' "$out/ashes.json"
