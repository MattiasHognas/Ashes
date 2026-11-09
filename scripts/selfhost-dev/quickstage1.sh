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
# Stage 1 calls libLLVM at run time through its $ORIGIN RUNPATH: the bundled copy stage 0 loads goes
# beside it, so both stages generate code with the same LLVM rather than whatever the system ships.
cp -u runtimes/linux-x64/lib*.so* "$J/"
for src in t0 t4; do
    rm -f "$J/tiny.out"
    bash -c "ulimit -s 1048576; '$J/s1-$tag' compile '$J/tiny/$src.ash' -o '$J/tiny.out'" > "$T/q-$tag.$src.log" 2>&1
    code=$?
    echo "$tag $src: exit=$code output=$([ -x "$J/tiny.out" ] && "$J/tiny.out" 2>&1 | head -1)"
done
# Stage-1 miscompile guards: programs stage 1 once compiled into crashing or leaking code, each run at two
# rounds with the leak counter, which must print the expected result and one live cell.
for guard in "$HERE"/smoke/*.ash; do
    name=$(basename "$guard" .ash)
    rm -f "$J/tiny.out"
    bash -c "ulimit -s 1048576; ASHES_RC_LEAKCHECK=1 '$J/s1-$tag' compile '$guard' -o '$J/tiny.out'" > "$T/q-$tag.$name.log" 2>&1
    output=$([ -x "$J/tiny.out" ] && timeout 60 "$J/tiny.out" 2>&1 | tr '\n' ' ')
    expected=$(head -1 "$guard" | sed -n 's/^\/\/ expect: //p')
    [ "$output" = "$expected ashes-rc-live 1 " ] && verdict=ok || verdict=FAIL
    echo "$tag guard $name: $verdict output=$output"
done
