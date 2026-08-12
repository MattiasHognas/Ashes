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

### Agent guidance is one document under two names, so tools that look for either find the same
### instructions. They are kept as real files rather than a symlink because a symlink materializes as
### a text file containing its target on a Windows checkout without developer mode, which would
### silently replace the guidance with a single line.
echo "--- Verifying agent guidance is in sync..."
if ! cmp -s AGENTS.md CLAUDE.md; then
  echo "AGENTS.md and CLAUDE.md must be identical; copy whichever you edited over the other." >&2
  diff -u AGENTS.md CLAUDE.md >&2 || true
  exit 1
fi

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

### Cross-compile smoke: win-arm64 is a compile-and-link-only target — Windows-on-ARM PE cannot be
### executed on an x64 CI host, so there is no run leg for it. Structural PE validation (machine
### 0xAA64, imports, resolved relocations) runs in the C# suite (WindowsArm64BackendTests); here we
### confirm the full toolchain emits an ARM64 PE end-to-end from source.
echo "--- Verifying win-arm64 cross-compilation..."
winArm64Src="$(mktemp --suffix=.ash)"
winArm64Out="$(mktemp --suffix=.exe)"
printf 'Ashes.IO.print("win-arm64 ok")\n' > "$winArm64Src"
"$ashesCli" compile --target win-arm64 "$winArm64Src" -o "$winArm64Out"
# PE machine field (COFF header at e_lfanew+4) must be IMAGE_FILE_MACHINE_ARM64 (0xAA64, little-endian 64 AA).
peOff=$(od -An -tu4 -j60 -N4 "$winArm64Out" | tr -d ' ')
machine=$(od -An -tx1 -j$((peOff + 4)) -N2 "$winArm64Out" | tr -d ' ')
[ "$machine" = "64aa" ] || { echo "win-arm64 PE machine mismatch: got $machine, want 64aa" >&2; exit 1; }
rm -f "$winArm64Src" "$winArm64Out"

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
"$ashesCli" test --pipeline both tests/*.ash

### Run test projects.
echo "--- Running test projects..."
for project in tests/**/*.json; do
  pushd "$(dirname "$project")" > /dev/null
  "$ashesCli" test --pipeline both --project "$(basename "$project")"
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
