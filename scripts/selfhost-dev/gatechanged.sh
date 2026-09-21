#!/bin/bash
# Runs the pull-request gate over every .ash file that differs from HEAD, and first confirms that regenerating
# the shared parity fixtures leaves them untouched.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
files=$(git diff --name-only ${BASE:-HEAD} -- "*.ash")
echo "changed .ash files: $(echo "$files" | wc -l)"
bash "$HERE/regenfixtures.sh" || exit 1
echo "fixture files changed by regeneration: $(git status --short selfhost/parity | wc -l)"
bash "$HERE/gatepr.sh" $files
