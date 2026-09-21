#!/bin/bash
# usage: publishcli.sh <label> ; publishes the current checkout's CLI as a self-contained single file into
# $J/cli-<label>, aborting on a failed publish, and smoke-tests it.
. "$(dirname "$0")/env.sh"
label=$1
cd "$R" || exit 1
rm -rf "$J/cli-$label"
dotnet publish src/Ashes.Cli/Ashes.Cli.csproj --configuration Release --runtime linux-x64 --self-contained true \
    -p:PublishSingleFile=true --output "$J/cli-$label" > "$T/publish-$label.log" 2>&1 \
    || { echo "PUBLISH FAILED"; grep -E " error " "$T/publish-$label.log" | sort -u | head -5; exit 1; }
printf 'Ashes.IO.print("published ok")\n' > "$J/cli-$label.smoke.ash"
"$J/cli-$label/ashes" compile "$J/cli-$label.smoke.ash" -o "$J/cli-$label.smoke" > /dev/null 2>&1
echo "$label: $(git rev-parse --short HEAD) compile exit=$? prints=$("$J/cli-$label.smoke" 2>&1 | head -1)"
