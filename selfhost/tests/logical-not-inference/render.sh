#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
out=work/logical-not-inference
mkdir -p "$out"
cp selfhost/tests/logical-not-inference/Main.ash "$out/Main.ash"
for name in CoreLogicalNotInferenceTests CoreOrderedInferenceTests; do
    cp "selfhost/tests/semantics/$name.ash" "$out/$name.ash"
done
cat > "$out/ashes.json" <<'JSON'
{"name":"logical-not-inference","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp selfhost/tests/semantics/ashes.lock "$out/ashes.lock"
