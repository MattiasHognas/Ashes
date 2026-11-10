#!/usr/bin/env bash
# Generate a diagnostic driver from current production traversal code, without building/running.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
droppers="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash"
fixture="$repo/selfhost/tests/admissibility-worklist"
out="$repo/work/admissibility-worklist"
# Fail on renamed or ambiguous extraction boundaries rather than silently testing stale code.
for marker in 'let recursive originalGeneralRcAdmissible ' 'let isGeneralRcAdmissible (semanticType:'; do
    [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
[[ $(rg -F -c 'let sameTypeKey ' "$droppers") == 1 ]]
[[ $(awk '/^\/\/ Stage 0.*IsGeneralRcAdmissible/ { count++ } END { print count+0 }' "$droppers") == 1 ]]
mkdir -p "$out"
cat "$fixture/FixturePrelude.ash.inc" > "$out/Main.ash"
awk '/^let sameTypeKey / { copying=1 } copying && /^\/\/ Stage 0.*IsGeneralRcAdmissible/ { exit } copying { print }' "$droppers" >> "$out/Main.ash"
awk '/^let recursive originalGeneralRcAdmissible / { copying=1 } copying && /^let isGeneralRcAdmissible \(semanticType:/ { exit } copying { print }' "$core" >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
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
