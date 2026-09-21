#!/bin/bash
# usage: suitetoggle.sh <suite> <ENV=val>... ; rebuilds the CLI once, then builds and runs one selfhost
# suite per toggle set given as separate arguments ("-" for none). Toggles are compile-time.
. "$(dirname "$0")/env.sh"
suite=$1; shift
cd "$R" || exit 1
dotnet build -c Release src/Ashes.Cli > "$T/suitetoggle.build" 2>&1 || { echo "CLI BUILD FAILED"; grep -E " error " "$T/suitetoggle.build" | sort -u | head -5; exit 1; }
for toggle in "$@"; do
    [ "$toggle" = "-" ] && toggle="_UNUSED=1"
    rm -rf "selfhost/tests/$suite/out"
    env $toggle dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "selfhost/tests/$suite/ashes.json" > "$T/suitetoggle.s.build" 2>&1
    bin=$(ls "selfhost/tests/$suite/out/" 2>/dev/null | head -1)
    if [ -z "$bin" ]; then echo "[$toggle] COMPILE FAILED"; continue; fi
    if [ "$suite" = backend ]; then "selfhost/tests/$suite/out/$bin" lib/Ashes > "$T/suitetoggle.out" 2>&1; else "selfhost/tests/$suite/out/$bin" > "$T/suitetoggle.out" 2>&1; fi
    echo "[$toggle] exit=$? last=$(tail -1 "$T/suitetoggle.out" | cut -c1-80)"
done
