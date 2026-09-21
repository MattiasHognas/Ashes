#!/bin/bash
# usage: debugstage1.sh <tag> ; builds stage 1 with debug information into $J/s1-<tag>-debug, for tinywatch.sh:
# addresses are deterministic per binary, so the census that names a leaked cell must come from this same binary.
. "$(dirname "$0")/env.sh"
tag=$1
cd "$R" || exit 1
rm -rf selfhost/packages/cli/out
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --debug --project selfhost/packages/cli/ashes.json > "$T/debugstage1-$tag.log" 2>&1
echo "build exit=$?"
BIN=$(ls selfhost/packages/cli/out/ 2>/dev/null | head -1)
[ -z "$BIN" ] && { tail -3 "$T/debugstage1-$tag.log"; exit 1; }
cp "selfhost/packages/cli/out/$BIN" "$J/s1-$tag-debug"
echo "$J/s1-$tag-debug"
