#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
ulimit -s 1048576
out=work/reach-count-index
cli=${ASHES_COUNT_TEST_CLI:?Set ASHES_COUNT_TEST_CLI to a validated stage0 compiler}
s1=${ASHES_COUNT_TEST_S1:-}
scope() { local cap=$1 tag=$2; shift 2; bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 /usr/bin/time -v -o "$out/$tag.time" "$@" >"$out/$tag.stdout" 2>"$out/$tag.stderr"; }
for file in selfhost/packages/semantics/src/AshesCompiler/Semantics/ResultReachSummaries.ash "$out/Main.ash" "$out/CountQueries.ash" "$out/ReferenceResultReachSummaries.ash"; do scope 8G "format-$(basename "$file")" "$cli" fmt "$file" -w; done
for mode in "$@"; do
    compiler=$cli; cap=8G; poison=''
    [[ $mode == poison ]] && poison=1
    if [[ $mode == s1 ]]; then compiler=$s1; cap=20G; fi
    scope "$cap" "$mode-compile" env ASHES_RC_LEAKCHECK=1 ASHES_RC_POISON="$poison" "$compiler" compile --project "$out/ashes.json" -o "$out/$mode"
    for case in check old-index10 old-index100 new-index10 new-index100 old100 old1000 new100 new1000 old-one new-one old-small new-small old-skew new-skew old-reverse-skew new-reverse-skew old-large new-large old-overlap new-overlap; do scope 4G "$mode-$case" "$out/$mode" "$case"; done
done
