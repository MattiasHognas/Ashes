#!/bin/bash
# usage: s1dumpat.sh <commit> <source.ash> <out-file> ; stage 1's lowered IR of a source file as the lowering stood at
# a commit: swaps that commit's lowering sources in, builds the dump tool, runs it, restores the working tree's files.
. "$(dirname "$0")/env.sh"
A=selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash
B=selfhost/packages/semantics/src/AshesCompiler/Semantics/MatchArmOwnership.ash
c=$1; src=$2; out=$3
cd "$R" || exit 1
cp "$A" "$J/s1dumpat.A.keep"; cp "$B" "$J/s1dumpat.B.keep"
git show "$c:$A" > "$A"; git show "$c:$B" > "$B"
rm -rf "$J/dumpir/out"
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "$J/dumpir/ashes.json" > "$T/s1dumpat.build" 2>&1
echo "dump tool at $c: build exit=$?"
cp "$J/s1dumpat.A.keep" "$A"; cp "$J/s1dumpat.B.keep" "$B"
BIN=$(ls "$J/dumpir/out/" | head -1)
bash -c "ulimit -s 1048576; '$J/dumpir/out/$BIN' '$src'" > "$out" 2>&1
echo "lines=$(wc -l < "$out"); restored tracked changes: $(git status --short "$A" "$B" | wc -l)"
# The binary left behind was built from the other commit's lowering. It is removed, so the next comparison stops
# and asks for a rebuild instead of silently measuring that commit.
rm -rf "$J/dumpir/out"
echo "dump tool removed: rebuild it with -b before the next comparison"
