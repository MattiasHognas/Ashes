#!/bin/bash
# usage: publishref.sh <git-ref> <label> ; publishes the CLI of another commit (origin/main, say) into $J/cli-<label>
# without touching this checkout: the commit's tree is exported to a scratch directory and built there, with this
# checkout's native runtimes copied in.
. "$(dirname "$0")/env.sh"
ref=$1; label=$2
S=$J/src-$label
rm -rf "$S" "$J/cli-$label"; mkdir -p "$S"
cd "$R" || exit 1
git archive "$ref" | tar -x -C "$S" || exit 1
cp -rn "$R/runtimes/." "$S/runtimes/"
cd "$S" || exit 1
dotnet publish src/Ashes.Cli/Ashes.Cli.csproj --configuration Release --runtime linux-x64 --self-contained true \
    -p:PublishSingleFile=true --output "$J/cli-$label" > "$T/publish-$label.log" 2>&1 \
    || { echo "PUBLISH FAILED"; grep -E " error " "$T/publish-$label.log" | sort -u | head -5; exit 1; }
echo "$label: $(git -C "$R" rev-parse --short "$ref") published to $J/cli-$label"
