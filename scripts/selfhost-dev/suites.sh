#!/bin/bash
# usage: suites.sh [name...] ; builds and runs the named self-hosted suites (all six by default).
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
run_suite() {
    name=$1; shift
    rm -rf "selfhost/tests/$name/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "selfhost/tests/$name/ashes.json" > "$T/suite-$name.build" 2>&1
    bin=$(ls "selfhost/tests/$name/out/" 2>/dev/null | head -1)
    if [ -z "$bin" ]; then echo "$name COMPILE FAILED: $(grep -E 'ASH|rror' "$T/suite-$name.build" | head -2 | cut -c1-200)"; return; fi
    "selfhost/tests/$name/out/$bin" "$@" > "$T/suite-$name.out" 2>&1
    code=$?
    echo "$name exit=$code fails=$(grep -ci 'fail' "$T/suite-$name.out") last=$(tail -1 "$T/suite-$name.out" | cut -c1-90)"
}
if [ $# -eq 0 ]; then
    run_suite semantics
    run_suite frontend
    run_suite formatter
    run_suite projects
    run_suite cli
    run_suite backend lib/Ashes
else
    for s in "$@"; do
        if [ "$s" = "backend" ]; then run_suite backend lib/Ashes; else run_suite "$s"; fi
    done
fi
