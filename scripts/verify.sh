#!/usr/bin/env bash

if [ -z "${BASH_VERSION:-}" ]; then
  if command -v bash >/dev/null 2>&1; then
    exec bash "$0" "$@"
  fi

  echo "verify.sh requires bash" >&2
  exit 1
fi

set -euo pipefail
shopt -s globstar nullglob

scriptDir="$(cd "$(dirname "$0")" && pwd)"
repoRoot="$(cd "${scriptDir}/.." && pwd)"
cd "$repoRoot"

## Determine the current OS
os="$(uname -s)"
corepackCmd="corepack"
pnpmCmd="pnpm"
useCorepackPnpm="false"
if [[ "$os" == "Linux" ]]; then
  os="linux"
  executableName="ashes"
  useCorepackPnpm="true"
elif [[ "$os" == "MINGW"* || "$os" == "CYGWIN"* || "$os" == "MSYS"* ]]; then
  os="win"
  executableName="ashes.exe"
else
  echo "Unsupported OS: $os"
  exit 1
fi

## Determine the current architecture

arch="$(uname -m)"
if [[ "$arch" == "x86_64" || "$arch" == "amd64" ]]; then
  arch="x64"
elif [[ "$arch" == "aarch64" || "$arch" == "arm64" ]]; then
  arch="arm64"
else
  echo "Unsupported architecture: $arch"
  exit 1
fi

## Verify the Ashes compiler, formatter, and language server work correctly by building, running tests, and formatting examples.

echo "--- Verifying Ashes on ${os}-${arch}..."

run_pnpm() {
  if [[ "$useCorepackPnpm" == "true" ]]; then
    "$corepackCmd" pnpm "$@"
  else
    "$pnpmCmd" "$@"
  fi
}

dotnet restore "Ashes.slnx"
dotnet build "Ashes.slnx" --configuration Release --no-restore
dotnet format "Ashes.slnx" --no-restore
dotnet run --project src/Ashes.Tests/Ashes.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project src/Ashes.Lsp.Tests/Ashes.Lsp.Tests.csproj --configuration Release --no-build --no-restore
dotnet publish src/Ashes.Cli/Ashes.Cli.csproj \
  --configuration Release \
  --runtime ${os}-${arch} \
  --self-contained true \
  -p:PublishSingleFile=true \
  --output artifacts/ashes/${os}-${arch}

## Verify the Ashes CLI can format examples and run tests.

ashesCli="${repoRoot}/artifacts/ashes/${os}-${arch}/${executableName}"

### Format all examples.
echo "--- Formatting examples..."
for example in examples/**/*.ash; do
  "$ashesCli" fmt "$example" -w
done

### Format all tests.
echo "--- Formatting tests..."
for test in tests/**/*.ash; do
  if grep -q '^//\s*fmt-skip:' "$test"; then
    continue
  fi
  "$ashesCli" fmt "$test" -w
done

## Run tests.
echo "--- Running tests..."
"$ashesCli" test tests/*.ash

### Run test projects.
echo "--- Running test projects..."
for project in tests/**/*.json; do
  pushd "$(dirname "$project")" > /dev/null
  "$ashesCli" test --project "$(basename "$project")"
  popd > /dev/null
done

## Verify the VS Code extension can be built and packaged.
echo "--- Verifying VS Code extension..."
cd vscode-extension
CI=true run_pnpm install --frozen-lockfile
run_pnpm run lint
run_pnpm run format:check
run_pnpm run compile
run_pnpm exec vsce package --no-dependencies --allow-missing-repository --skip-license --out ../ashes-vscode.vsix
