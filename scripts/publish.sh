#!/usr/bin/env bash
# Publish self-contained Ashes compiler executables for win-x64 and linux-x64.
# Outputs: dist/win-x64/ashes.exe  and  dist/linux-x64/ashes
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$REPO_ROOT/src/Ashes.Cli/Ashes.Cli.csproj"
LIB_SOURCE="$REPO_ROOT/lib"

# Version to embed into the published binaries.
# Can be overridden by setting the VERSION environment variable.
VERSION="${VERSION:-0.0.0-dev}"

echo "Restoring..."
dotnet restore "$CLI_PROJECT"

for RID in win-x64 linux-x64; do
  echo "Publishing $RID (version $VERSION)..."
  OUTPUT_DIR="$REPO_ROOT/dist/$RID"
  dotnet publish "$CLI_PROJECT" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:Version="$VERSION" \
    --output "$OUTPUT_DIR" \
    --no-restore

  rm -rf "$OUTPUT_DIR/lib"
  # Copy standard library .ash files but exclude native LLVM binaries
  # (the self-contained publish already includes libLLVM in the app root).
  rsync -a --exclude='linux-x64/' --exclude='win-x64/' "$LIB_SOURCE/" "$OUTPUT_DIR/lib/"
done

chmod +x "$REPO_ROOT/dist/linux-x64/ashes"

echo "Done."
echo "  dist/win-x64/ashes.exe"
echo "  dist/linux-x64/ashes"
