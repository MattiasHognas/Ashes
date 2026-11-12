#!/usr/bin/env bash
# Generate a diagnostic driver from current production traversal code, without building/running.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="${1:-$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash}"
droppers="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash"
fixture="$repo/selfhost/tests/admissibility-worklist"
out="$repo/work/admissibility-worklist"
# Fail on renamed or ambiguous extraction boundaries rather than silently testing stale code.
for marker in 'type GeneralRcAdmissibilityWork =' 'let isGeneralRcAdmissibleIndexed (semanticType:'; do
    [[ $(rg -F -c "$marker" "$core") == 1 ]]
done
[[ $(rg -F -c 'let sameTypeKey ' "$droppers") == 1 ]]
[[ $(awk '/^\/\/ Stage 0.*IsGeneralRcAdmissible/ { count++ } END { print count+0 }' "$droppers") == 1 ]]
mkdir -p "$out"
cat "$fixture/FixturePrelude.ash.inc" > "$out/Main.ash"
awk '/^let sameTypeKey / { copying=1 } copying && /^\/\/ Stage 0.*IsGeneralRcAdmissible/ { exit } copying { print }' "$droppers" >> "$out/Main.ash"
python3 - "$repo" "$core" "$out" <<'PY'
from pathlib import Path
import hashlib
import re
import subprocess
import sys

repo, core, out = map(Path, sys.argv[1:])
reference_commit = 'cc50f0f52b4ee76ffa0f96ee3c70e288bd273abd'
source_path = 'selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash'
reference_source = subprocess.check_output(['git', '-C', str(repo), 'show', reference_commit + ':' + source_path], text=True)
source = core.read_text()

def extract(text, start, end):
    assert text.count(start) == text.count(end) == 1, 'ambiguous extraction boundaries'
    begin, finish = text.index(start), text.index(end)
    assert begin < finish, 'reversed extraction boundaries'
    return text[begin:finish]

reference = extract(reference_source, 'let recursive originalGeneralRcAdmissible ', 'type GeneralRcAdmissibilityWork =')
for name in ['originalGeneralRcAdmissible', 'originalAllGeneralRcAdmissible', 'originalAllChildrenGeneralRcAdmissible']:
    reference = re.sub(r'\b' + name + r'\b', 'reference' + name[0].upper() + name[1:], reference)
candidate = extract(source, 'type GeneralRcAdmissibilityWork =', 'let isGeneralRcAdmissibleIndexed (semanticType:')
with (out / 'Main.ash').open('a') as generated:
    generated.write(reference)
    generated.write(candidate)
(out / 'candidate-extracted.ash.inc').write_text(candidate)
(out / 'reference-extracted.ash.inc').write_text(reference)
(out / 'reference-source.txt').write_text(reference_commit + ':' + source_path + '\nsha256 ' + hashlib.sha256(reference_source.encode()).hexdigest() + '\n')
(out / 'candidate-source.txt').write_text(str(core.resolve()) + '\nsha256 ' + hashlib.sha256(source.encode()).hexdigest() + '\n')
PY
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
