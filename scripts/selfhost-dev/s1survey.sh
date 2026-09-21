#!/bin/bash
# usage: s1survey.sh [-b] ; every shared lowered-IR fixture, stage 1 against stage 0: the fixtures that differ once
# numbers are normalised (a missing rule), and the ones that differ only in temp, local or label numbers (a rule
# that allocates in another order). A port is checked against all of them at once, regressions included.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
names=$(ls selfhost/parity/semantics/lowered-ir/*.source | xargs -n1 basename | sed 's/\.source$//')
build=""
if [ "$1" = "-b" ]; then build="-b"; fi
SHOW=0 bash "$HERE/s1diff.sh" $build $names > "$T/s1survey.txt" 2>&1
grep -E '^dump tool build' "$T/s1survey.txt"
grep '^==' "$T/s1survey.txt" | grep -v 'stage0-only lines=0 stage1-only lines=0'
echo "-- numbering only:"
for name in $names; do
    if grep -q "^== $name: stage0-only lines=0 stage1-only lines=0" "$T/s1survey.txt"; then
        raw=$(bash "$HERE/rawdiff.sh" "$name" 1000000 | grep -c '^[<>]')
        [ "$raw" != "0" ] && echo "== $name: raw differing lines=$raw"
    fi
done
echo "survey done: $(echo "$names" | wc -w) fixtures"
