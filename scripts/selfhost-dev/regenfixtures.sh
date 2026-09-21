#!/bin/bash
# Builds the test project (aborting on failure), regenerates the shared parity fixtures from stage 0, and shows
# which fixture files changed. Then runs the parity tests once more without the update switch to confirm they pass.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
dotnet build -c Release Ashes.slnx > "$T/regen-build.log" 2>&1 || { echo "BUILD FAILED"; grep -E " error " "$T/regen-build.log" | sort -u | head -5 | cut -c1-250; exit 1; }
ASHES_UPDATE_PARITY_FIXTURES=1 dotnet run -c Release --no-build --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/SelfhostIrParityTests/**" > "$T/regen-update.log" 2>&1
echo "update run exit=$?"
git status --short selfhost/parity src/Ashes.Tests/Fixtures | cut -c1-120
dotnet run -c Release --no-build --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/SelfhostIrParityTests/**" > "$T/regen-verify.log" 2>&1
echo "verify exit=$? $(grep -iE 'total:|failed:|succeeded:' "$T/regen-verify.log" | tr -s ' \n' ' ')"
