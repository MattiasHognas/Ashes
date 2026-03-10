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
enableCorepack="false"
if [[ "$os" == "Linux" ]]; then
  os="linux"
  executableName="ashes"
  enableCorepack="true"
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

## Verify the Ashes compiler, formatter, and language server work correctly by building, running tests, and running examples.

echo "--- Verifying Ashes on ${os}-${arch}..."
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

## Verify the Ashes CLI can format and run examples and tests.

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

### Run examples.
echo "--- Running examples..."
for example in examples/*.ash; do
  "$ashesCli" run "$example" < /dev/null
done

### Run example projects.
echo "--- Running example projects..."
for project in examples/**/*.json; do
  pushd "$(dirname "$project")" > /dev/null
  "$ashesCli" run --project "$(basename "$project")" < /dev/null
  popd > /dev/null
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
if [[ "$enableCorepack" == "true" ]]; then
  "$corepackCmd" enable
fi
"$pnpmCmd" install --frozen-lockfile --force
"$pnpmCmd" run lint
"$pnpmCmd" run format:check
"$pnpmCmd" run compile
"$pnpmCmd" run build-server
"$pnpmCmd" dlx '--config.ignoredBuiltDependencies[]=@vscode/vsce-sign' '--config.ignoredBuiltDependencies[]=keytar' @vscode/vsce@3.7.1 package --no-dependencies --allow-missing-repository --skip-license --out ../ashes-vscode.vsix
