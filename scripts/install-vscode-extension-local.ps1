param(
    [string]$CodeCommand,
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ExtensionRoot = Join-Path $RepoRoot "vscode-extension"
$CompilerRoot = Join-Path $ExtensionRoot "compiler"
$LspServerRoot = Join-Path $ExtensionRoot "lsp-server"
$DapServerRoot = Join-Path $ExtensionRoot "dap-server"
$VsixPath = Join-Path $RepoRoot "ashes-vscode-local.vsix"

function Resolve-CodeCommand {
    param([string]$RequestedCommand)

    if ($RequestedCommand) {
        if (-not (Get-Command $RequestedCommand -ErrorAction SilentlyContinue)) {
            throw "VS Code CLI '$RequestedCommand' was not found on PATH."
        }

        return $RequestedCommand
    }

    foreach ($candidate in @("code-insiders", "code")) {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) {
            return $candidate
        }
    }

    throw "VS Code CLI was not found on PATH. Install the 'code' command, or pass -CodeCommand explicitly."
}

function Resolve-PnpmCommand {
    if (Get-Command "pnpm" -ErrorAction SilentlyContinue) {
        return "pnpm"
    }

    throw "pnpm was not found on PATH. Install pnpm or enable it through your Node.js setup before running this script."
}

function Invoke-Step {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [string]$Description
    )

    Write-Host "==> $Description"
    Write-Host "    $FilePath $($ArgumentList -join ' ')"

    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-ExtensionVersion {
    $packageJsonPath = Join-Path $ExtensionRoot "package.json"
    $packageJson = Get-Content $packageJsonPath -Raw | ConvertFrom-Json
    if (-not $packageJson.version) {
        throw "Unable to determine VS Code extension version from $packageJsonPath"
    }

    return [string]$packageJson.version
}

function Publish-Compiler {
    param(
        [string]$Rid,
        [string]$Version
    )

    $outputDir = Join-Path $CompilerRoot $Rid
    if (Test-Path $outputDir) {
        Remove-Item $outputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    Invoke-Step `
        -FilePath "dotnet" `
        -ArgumentList @(
            "publish",
            "src/Ashes.Cli/Ashes.Cli.csproj",
            "--configuration", "Debug",
            "--runtime", $Rid,
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:Version=$Version",
            "--output", $outputDir
        ) `
        -WorkingDirectory $RepoRoot `
        -Description "Publishing Ashes CLI for $Rid (Debug)"
}

function Publish-LanguageServer {
    param(
        [string]$Rid,
        [string]$Version
    )

    $outputDir = Join-Path $LspServerRoot $Rid
    if (Test-Path $outputDir) {
        Remove-Item $outputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    Invoke-Step `
        -FilePath "dotnet" `
        -ArgumentList @(
            "publish",
            "src/Ashes.Lsp/Ashes.Lsp.csproj",
            "--configuration", "Debug",
            "--runtime", $Rid,
            "-p:UseAppHost=true",
            "--self-contained", "false",
            "-p:Version=$Version",
            "--output", $outputDir
        ) `
        -WorkingDirectory $RepoRoot `
        -Description "Publishing Ashes language server for $Rid (Debug)"
}

function Publish-DapServer {
    param(
        [string]$Rid,
        [string]$Version
    )

    $outputDir = Join-Path $DapServerRoot $Rid
    if (Test-Path $outputDir) {
        Remove-Item $outputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    Invoke-Step `
        -FilePath "dotnet" `
        -ArgumentList @(
            "publish",
            "src/Ashes.Dap/Ashes.Dap.csproj",
            "--configuration", "Debug",
            "--runtime", $Rid,
            "-p:UseAppHost=true",
            "--self-contained", "false",
            "-p:Version=$Version",
            "--output", $outputDir
        ) `
        -WorkingDirectory $RepoRoot `
        -Description "Publishing Ashes DAP server for $Rid (Debug)"
}

$version = Get-ExtensionVersion
$pnpmCommand = Resolve-PnpmCommand
$resolvedCodeCommand = if ($SkipInstall) { $null } else { Resolve-CodeCommand -RequestedCommand $CodeCommand }

New-Item -ItemType Directory -Force -Path $CompilerRoot | Out-Null
New-Item -ItemType Directory -Force -Path $LspServerRoot | Out-Null
New-Item -ItemType Directory -Force -Path $DapServerRoot | Out-Null

Publish-Compiler -Rid "win-x64" -Version $version
Publish-Compiler -Rid "linux-x64" -Version $version
Publish-Compiler -Rid "linux-arm64" -Version $version
Publish-LanguageServer -Rid "win-x64" -Version $version
Publish-LanguageServer -Rid "linux-x64" -Version $version
Publish-LanguageServer -Rid "linux-arm64" -Version $version
Publish-DapServer -Rid "win-x64" -Version $version
Publish-DapServer -Rid "linux-x64" -Version $version
Publish-DapServer -Rid "linux-arm64" -Version $version

Invoke-Step `
    -FilePath $pnpmCommand `
    -ArgumentList @("install", "--frozen-lockfile") `
    -WorkingDirectory $ExtensionRoot `
    -Description "Restoring VS Code extension dependencies"

Invoke-Step `
    -FilePath $pnpmCommand `
    -ArgumentList @("run", "compile") `
    -WorkingDirectory $ExtensionRoot `
    -Description "Building VS Code extension"

if (Test-Path $VsixPath) {
    Remove-Item $VsixPath -Force
}

Invoke-Step `
    -FilePath $pnpmCommand `
    -ArgumentList @(
        "dlx",
        "--config.ignoredBuiltDependencies[]=@vscode/vsce-sign",
        "--config.ignoredBuiltDependencies[]=keytar",
        "@vscode/vsce@3.9.1",
        "package",
        "--no-dependencies",
        "--allow-missing-repository",
        "--skip-license",
        "--out", $VsixPath
    ) `
    -WorkingDirectory $ExtensionRoot `
    -Description "Packaging local VSIX"

if (-not $SkipInstall) {
    Invoke-Step `
        -FilePath $resolvedCodeCommand `
        -ArgumentList @("--install-extension", $VsixPath, "--force") `
        -WorkingDirectory $RepoRoot `
        -Description "Installing local VSIX into VS Code"
}

Write-Host ""
Write-Host "Local VSIX ready: $VsixPath"
if ($SkipInstall) {
    Write-Host "Installation was skipped."
} else {
    Write-Host "Installed with: $resolvedCodeCommand --install-extension $VsixPath --force"
}