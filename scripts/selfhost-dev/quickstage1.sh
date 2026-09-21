#!/bin/bash
# usage: quickstage1.sh <tag> [ENV=val...] ; rebuilds the CLI (aborting on failure), builds stage 1 with
# the toggles, and compiles two small programs with it: a seconds-long crash detector. Prints each exit
# status and the produced program's output.
. "$(dirname "$0")/env.sh"
tag=$1; shift
[ $# -eq 0 ] && set -- _UNUSED=1
cd "$R" || exit 1
dotnet build -c Release src/Ashes.Cli > "$T/q-$tag.clibuild" 2>&1 || { echo "CLI BUILD FAILED"; grep -E " error " "$T/q-$tag.clibuild" | sort -u | head -5; exit 1; }
rm -rf selfhost/packages/cli/out; rm -f "$J/s1-$tag"
env "$@" dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project selfhost/packages/cli/ashes.json > "$T/q-$tag.s1build" 2>&1
BIN=$(ls selfhost/packages/cli/out/ 2>/dev/null | head -1)
[ -z "$BIN" ] && { echo "STAGE 1 BUILD FAILED"; grep -E "ASH|rror|nsupported" "$T/q-$tag.s1build" | head -3 | cut -c1-300; exit 1; }
cp "selfhost/packages/cli/out/$BIN" "$J/s1-$tag"
for src in t0 t4; do
    rm -f "$J/tiny.out"
    bash -c "ulimit -s 1048576; '$J/s1-$tag' compile '$J/tiny/$src.ash' -o '$J/tiny.out'" > "$T/q-$tag.$src.log" 2>&1
    code=$?
    echo "$tag $src: exit=$code output=$([ -x "$J/tiny.out" ] && "$J/tiny.out" 2>&1 | head -1)"
done
