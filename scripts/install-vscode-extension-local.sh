#!/usr/bin/env bash

if [ -z "${BASH_VERSION:-}" ]; then
  if command -v bash >/dev/null 2>&1; then
    exec bash "$0" "$@"
  fi

  echo "install-vscode-extension-local.sh requires bash" >&2
  exit 1
fi

set -euo pipefail

## Parse arguments
codeCommand=""
skipInstall="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --code-command)
      codeCommand="$2"
      shift 2
      ;;
    --skip-install)
      skipInstall="true"
      shift
      ;;
    -h|--help)
      echo "Usage: $0 [--code-command <cmd>] [--skip-install]"
      echo ""
      echo "  --code-command <cmd>  VS Code CLI to use (default: auto-detect code-insiders or code)"
      echo "  --skip-install        Build and package only, do not install the VSIX"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

## Paths
scriptDir="$(cd "$(dirname "$0")" && pwd)"
repoRoot="$(cd "${scriptDir}/.." && pwd)"
extensionRoot="${repoRoot}/vscode-extension"
compilerRoot="${extensionRoot}/compiler"
lspServerRoot="${extensionRoot}/lsp-server"
dapServerRoot="${extensionRoot}/dap-server"
vsixPath="${repoRoot}/ashes-vscode-local.vsix"

## Resolve commands

resolve_code_command() {
  if [[ -n "$codeCommand" ]]; then
    if ! command -v "$codeCommand" >/dev/null 2>&1; then
      echo "VS Code CLI '${codeCommand}' was not found on PATH." >&2
      exit 1
    fi
    echo "$codeCommand"
    return
  fi

  for candidate in code-insiders code; do
    if command -v "$candidate" >/dev/null 2>&1; then
      echo "$candidate"
      return
    fi
  done

  echo "VS Code CLI was not found on PATH. Install the 'code' command, or pass --code-command explicitly." >&2
  exit 1
}

resolve_pnpm_command() {
  if command -v pnpm >/dev/null 2>&1; then
    echo "pnpm"
    return
  fi

  echo "pnpm was not found on PATH. Install pnpm or enable it through your Node.js setup before running this script." >&2
  exit 1
}

## Helpers

invoke_step() {
  local description="$1"
  shift
  echo "==> ${description}"
  echo "    $*"
  "$@"
}

get_extension_version() {
  local packageJsonPath="${extensionRoot}/package.json"
  local version
  version="$(node -p "require('${packageJsonPath}').version" 2>/dev/null)" || true
  if [[ -z "$version" ]]; then
    echo "Unable to determine VS Code extension version from ${packageJsonPath}" >&2
    exit 1
  fi
  echo "$version"
}

## Publish functions

publish_compiler() {
  local rid="$1"
  local version="$2"
  local outputDir="${compilerRoot}/${rid}"

  rm -rf "$outputDir"
  mkdir -p "$outputDir"

  invoke_step "Publishing Ashes CLI for ${rid} (Debug)" \
    dotnet publish src/Ashes.Cli/Ashes.Cli.csproj \
      --configuration Debug \
      --runtime "$rid" \
      --self-contained true \
      -p:PublishSingleFile=true \
      "-p:Version=${version}" \
      --output "$outputDir"
}

publish_language_server() {
  local rid="$1"
  local version="$2"
  local outputDir="${lspServerRoot}/${rid}"

  rm -rf "$outputDir"
  mkdir -p "$outputDir"

  invoke_step "Publishing Ashes language server for ${rid} (Debug)" \
    dotnet publish src/Ashes.Lsp/Ashes.Lsp.csproj \
      --configuration Debug \
      --runtime "$rid" \
      -p:UseAppHost=true \
      --self-contained false \
      "-p:Version=${version}" \
      --output "$outputDir"
}

publish_dap_server() {
  local rid="$1"
  local version="$2"
  local outputDir="${dapServerRoot}/${rid}"

  rm -rf "$outputDir"
  mkdir -p "$outputDir"

  invoke_step "Publishing Ashes DAP server for ${rid} (Debug)" \
    dotnet publish src/Ashes.Dap/Ashes.Dap.csproj \
      --configuration Debug \
      --runtime "$rid" \
      -p:UseAppHost=true \
      --self-contained false \
      "-p:Version=${version}" \
      --output "$outputDir"
}

## Determine the current RID

os="$(uname -s)"
if [[ "$os" == "Linux" ]]; then
  os="linux"
elif [[ "$os" == "MINGW"* || "$os" == "CYGWIN"* || "$os" == "MSYS"* ]]; then
  os="win"
else
  echo "Unsupported OS: $os" >&2
  exit 1
fi

arch="$(uname -m)"
if [[ "$arch" == "x86_64" || "$arch" == "amd64" ]]; then
  arch="x64"
elif [[ "$arch" == "aarch64" || "$arch" == "arm64" ]]; then
  arch="arm64"
else
  echo "Unsupported architecture: $arch" >&2
  exit 1
fi

rid="${os}-${arch}"

## Main

cd "$repoRoot"

version="$(get_extension_version)"
pnpmCommand="$(resolve_pnpm_command)"
resolvedCodeCommand=""
if [[ "$skipInstall" == "false" ]]; then
  resolvedCodeCommand="$(resolve_code_command)"
fi

mkdir -p "$compilerRoot" "$lspServerRoot" "$dapServerRoot"

publish_compiler "$rid" "$version"
publish_language_server "$rid" "$version"
publish_dap_server "$rid" "$version"

cd "$extensionRoot"

if [[ "$os" == "linux" ]]; then
  corepack enable
fi

invoke_step "Restoring VS Code extension dependencies" \
  "$pnpmCommand" install --frozen-lockfile --force

invoke_step "Building VS Code extension" \
  "$pnpmCommand" run compile

rm -f "$vsixPath"

invoke_step "Packaging local VSIX" \
  "$pnpmCommand" dlx \
    '--config.ignoredBuiltDependencies[]=@vscode/vsce-sign' \
    '--config.ignoredBuiltDependencies[]=keytar' \
    @vscode/vsce@3.7.1 \
    package \
    --no-dependencies \
    --allow-missing-repository \
    --skip-license \
    --out "$vsixPath"

if [[ "$skipInstall" == "false" ]]; then
  invoke_step "Installing local VSIX into VS Code" \
    "$resolvedCodeCommand" --install-extension "$vsixPath" --force
fi

echo ""
echo "Local VSIX ready: ${vsixPath}"
if [[ "$skipInstall" == "true" ]]; then
  echo "Installation was skipped."
else
  echo "Installed with: ${resolvedCodeCommand} --install-extension ${vsixPath} --force"
fi
