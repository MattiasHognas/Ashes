#!/bin/bash
# Compiles the whole-program IR parity suite with the built CLI and runs it; prints its verdict and any mismatch.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
rm -f "$J/ir-program-parity"
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project selfhost/tests/ir-program-parity/ashes.json -o "$J/ir-program-parity" > "$T/parity-build.log" 2>&1
echo "build exit=$? $(grep -E 'ASH[0-9]+|rror' "$T/parity-build.log" | head -3 | cut -c1-220)"
bash -c "ulimit -s 1048576; '$J/ir-program-parity' selfhost/parity/semantics/lowered-ir" > "$T/parity.log" 2>&1
echo "run exit=$?"
tail -12 "$T/parity.log" | cut -c1-230
