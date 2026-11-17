#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
compiler=$(realpath -- "${1:?compiler path}")
tag=${2:?unique evidence tag}
mode=${3:-plain}
[[ $tag =~ ^[a-zA-Z0-9_-]+$ ]]
case "$mode" in
    plain) unset ASHES_RC_POISON ;;
    poison) export ASHES_RC_POISON=1 ;;
    *) exit 2 ;;
esac
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 ASHES_RC_LEAKCHECK=1
ulimit -s 1048576
bash selfhost/tests/constrained-specialization/render.sh
base="work/constrained-specialization-$tag"
mkdir "$base"
sha256sum "$compiler" selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreConstrainedSpecialization.ash selfhost/tests/semantics/CoreConstrainedSpecializationTests.ash selfhost/tests/semantics/CoreConstrainedSpecializationInferenceTests.ash selfhost/tests/constrained-specialization/Main.ash > "$base/inputs.sha256"
bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 /usr/bin/time -v -o "$base/compile.time" "$compiler" compile --project work/constrained-specialization/check/ashes.json -o "$base/native" > "$base/compile.stdout" 2> "$base/compile.stderr"
for count in 1 1000 10000; do
    systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 /usr/bin/time -v -o "$base/$count.time" "$base/native" "$count" > "$base/$count.stdout" 2> "$base/$count.stderr"
    [[ $(cat "$base/$count.stdout") == 'concrete planning passed' ]]
    [[ $(cat "$base/$count.stderr") =~ ^ashes-rc-live\ [0-9]+$ ]]
    printf '%s: %s\n' "$count" "$(cat "$base/$count.stderr")"
done
cmp "$base/1.stderr" "$base/1000.stderr"
cmp "$base/1.stderr" "$base/10000.stderr"
sha256sum -c "$base/inputs.sha256"
