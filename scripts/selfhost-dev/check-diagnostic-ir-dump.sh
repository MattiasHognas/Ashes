#!/usr/bin/env bash
# Run an already compiled selfhost/tests/diagnostic-ir-dump binary. Never removes evidence.
set -euo pipefail
binary=$(realpath "$1")
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
mkdir -p "$repo/work"
out=$(mktemp -d "$repo/work/diagnostic-ir-dump.XXXXXX")
ulimit -s 1048576
run() { systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 "$@"; }
run env -u ASHES_DIAGNOSTIC_IR_PREFIX ASHES_DIAGNOSTIC_IR_FILTER=selected "$binary" > "$out/disabled.out" 2> "$out/disabled.err"
rg -x 'ok' "$out/disabled.out"
run env ASHES_DIAGNOSTIC_IR_PREFIX= ASHES_DIAGNOSTIC_IR_FILTER=selected "$binary" > "$out/empty.out" 2> "$out/empty.err"
rg -x 'ok' "$out/empty.out"
if compgen -G "$out/*.ir" > /dev/null; then exit 1; fi
for filter in '' SeLeCt sourcename Module.Source ParentLabel missing; do
 name=${filter:-all}
 run env ASHES_DIAGNOSTIC_IR_PREFIX="$out/$name" ASHES_DIAGNOSTIC_IR_FILTER="$filter" "$binary" > "$out/$name.out" 2> "$out/$name.err"
 rg -x 'ok' "$out/$name.out"
 run "$binary" oracle-lowered "$filter" > "$out/$name.expected-lowered" 2> "$out/$name.oracle-lowered.err"
 run "$binary" oracle-final "$filter" > "$out/$name.expected-final" 2> "$out/$name.oracle-final.err"
 cmp "$out/$name.expected-lowered" "$out/$name.lowered.ir"
 cmp "$out/$name.expected-final" "$out/$name.final.ir"
done
for name in SeLeCt sourcename Module.Source ParentLabel; do
 rg '^function selected' "$out/$name.lowered.ir"
 ! rg '^function (main|other)' "$out/$name.lowered.ir"
done
rg -F '(no functions matched)' "$out/missing.final.ir"
run env ASHES_DIAGNOSTIC_IR_PREFIX="$out/absent/blocked" "$binary" > "$out/error.out" 2> "$out/error.err"
rg -F 'Could not write diagnostic IR ' "$out/error.out"
test ! -e "$out/absent/blocked.final.ir"
mkdir "$out/second-error.final.ir"
run env ASHES_DIAGNOSTIC_IR_PREFIX="$out/second-error" "$binary" > "$out/second-error.out" 2> "$out/second-error.err"
rg -F 'Could not write diagnostic IR ' "$out/second-error.out"
test -f "$out/second-error.lowered.ir"
if rg -q '^ashes-rc-live ' "$out/disabled.err"; then
 count() { awk '/^ashes-rc-live / {print $2}' "$1"; }
 baseline=$(count "$out/disabled.err")
 oracle_baseline=$(count "$out/missing.oracle-lowered.err")
 test "$(count "$out/empty.err")" = "$baseline"
 for name in all SeLeCt sourcename Module.Source ParentLabel missing; do
  lowered=$(count "$out/$name.oracle-lowered.err")
  final=$(count "$out/$name.oracle-final.err")
  test "$(count "$out/$name.err")" = "$((baseline + lowered + final - 2 * oracle_baseline))"
 done
 test "$(count "$out/error.err")" = "$((baseline + $(count "$out/all.oracle-lowered.err") - oracle_baseline))"
 test "$(count "$out/second-error.err")" = "$((baseline + $(count "$out/all.oracle-lowered.err") + $(count "$out/all.oracle-final.err") - 2 * oracle_baseline))"
fi
printf 'Diagnostic IR checks passed; evidence: %s\n' "$out"
