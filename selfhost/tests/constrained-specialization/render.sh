#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
base=work/constrained-specialization/check
mkdir -p "$base"
cp selfhost/tests/constrained-specialization/Main.ash "$base/Main.ash"
cp selfhost/tests/semantics/CoreConstrainedSpecializationTests.ash "$base/Tests.ash"
cp selfhost/tests/semantics/CoreConstrainedSpecializationInferenceTests.ash "$base/InferenceTests.ash"
cp selfhost/tests/semantics/ashes.lock "$base/ashes.lock"
cat > "$base/ashes.json" <<'JSON'
{"name":"constrained-copy-planning","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../../selfhost/packages/frontend"}}}
JSON
