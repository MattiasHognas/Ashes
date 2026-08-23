#!/usr/bin/env bash
# Updates all selfhost lowered IR parity fixtures (.ir) from their .source files.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Regenerating lowered IR parity fixtures from .source files..."
ASHES_UPDATE_PARITY_FIXTURES=1 dotnet run --project "${REPO_ROOT}/src/Ashes.Tests" -- --no-progress --treenode-filter "/*/*/SelfhostIrParityTests/**"
echo "All lowered IR fixtures successfully regenerated."
