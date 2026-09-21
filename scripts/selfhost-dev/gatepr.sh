#!/bin/bash
# usage: gatepr.sh [changed .ash files...] ; the landing gate for a step C pull request, run once per PR:
# build, C# format, canonical .ash formatting, the six selfhost suites, whole-program IR parity, Ashes.Tests
# (failing names listed, none expected on a branch cut from main), LSP, e2e plain and under ASHES_RC_POISON=1.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
dotnet build -c Release Ashes.slnx > "$T/gp-build.log" 2>&1; echo "build exit=$? $(grep -E 'Error\(s\)|Warn.*\(s\)' "$T/gp-build.log" | tr '\n' ' ')"
grep -q " 0 Error(s)" "$T/gp-build.log" || { grep -E " error " "$T/gp-build.log" | sort -u | head -5 | cut -c1-250; exit 1; }
dotnet format Ashes.slnx --verify-no-changes > "$T/gp-format.log" 2>&1; echo "format exit=$?"
for f in "$@"; do
    cp "$f" "$J/gp.before"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- fmt "$f" -w > /dev/null 2>&1
    echo "  fmt exit=$? reformatted-lines=$(diff "$J/gp.before" "$f" | grep -c '^[<>]') $f"
done
echo "selfhost suites:"
bash "$HERE/suites.sh"
rm -f "$J/ir-program-parity"
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project selfhost/tests/ir-program-parity/ashes.json -o "$J/ir-program-parity" > "$T/gp-parity-build.log" 2>&1
echo "ir-program-parity build exit=$?"
bash -c "ulimit -s 1048576; '$J/ir-program-parity' selfhost/parity/semantics/lowered-ir" > "$T/gp-parity.log" 2>&1
echo "ir-program-parity exit=$? last=$(tail -1 "$T/gp-parity.log" | cut -c1-120)"
dotnet run -c Release --no-build --project src/Ashes.Tests -- --no-progress > "$T/gp-tests.log" 2>&1; echo "Ashes.Tests exit=$? $(grep -iE 'total:|failed:|succeeded:' "$T/gp-tests.log" | tr -s ' \n' ' ')"
grep -oE '^failed [A-Za-z0-9_]+' "$T/gp-tests.log" | sort | uniq -c | head -12
dotnet run -c Release --no-build --project src/Ashes.Lsp.Tests -- --no-progress > "$T/gp-lsp.log" 2>&1; echo "Lsp.Tests exit=$? $(grep -iE 'total:|succeeded:|failed:' "$T/gp-lsp.log" | tr -s ' \n' ' ')"
dotnet run -c Release --no-build --project src/Ashes.Cli -- test tests > "$T/gp-e2e.log" 2>&1; echo "e2e exit=$? $(tail -1 "$T/gp-e2e.log")"
ASHES_RC_POISON=1 dotnet run -c Release --no-build --project src/Ashes.Cli -- test tests > "$T/gp-e2e-poison.log" 2>&1; echo "e2e POISON exit=$? $(tail -1 "$T/gp-e2e-poison.log")"
