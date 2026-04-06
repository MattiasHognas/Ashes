#Requires -Version 7.0
param()

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir

Set-Location $repoRoot

function Invoke-Step {
    param(
        [string]$Description,
        [scriptblock]$Command
    )

    Write-Host "--- $Description"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE"
    }
}

## Determine the current OS and architecture

$isWindowsHost = $IsWindows -or $env:OS -eq "Windows_NT"
$isLinuxHost = $IsLinux -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
$isMacOSHost = $IsMacOS -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)

$osName = if ($isWindowsHost) {
    "win"
} elseif ($isLinuxHost) {
    "linux"
} elseif ($isMacOSHost) {
    throw "Unsupported OS: macOS. verify.ps1 supports Windows and Linux only."
} else {
    throw "Unsupported OS. verify.ps1 supports Windows and Linux only."
}
$executableName = if ($osName -eq "win") { "ashes.exe" } else { "ashes" }

$archRaw = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$arch = switch ($archRaw) {
    "x64"   { "x64" }
    "arm64" { "arm64" }
    default { throw "Unsupported architecture: $archRaw" }
}

$rid = "${osName}-${arch}"

## Verify the Ashes compiler, formatter, and language server work correctly.

Write-Host "--- Verifying Ashes on ${rid}..."

Invoke-Step "Restoring packages" {
    dotnet restore "Ashes.slnx"
}

Invoke-Step "Building solution" {
    dotnet build "Ashes.slnx" --configuration Release --no-restore
}

Invoke-Step "Checking formatting" {
    dotnet format "Ashes.slnx" --no-restore
}

Invoke-Step "Running Ashes.Tests" {
    dotnet run --project src/Ashes.Tests/Ashes.Tests.csproj --configuration Release --no-build --no-restore
}

Invoke-Step "Running Ashes.Lsp.Tests" {
    dotnet run --project src/Ashes.Lsp.Tests/Ashes.Lsp.Tests.csproj --configuration Release --no-build --no-restore
}

Invoke-Step "Publishing Ashes CLI for ${rid}" {
    dotnet publish src/Ashes.Cli/Ashes.Cli.csproj `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        --output "artifacts/ashes/${rid}"
}

## Verify the Ashes CLI can format and run examples and tests.

$ashesCli = Join-Path $repoRoot "artifacts" "ashes" $rid $executableName

### Format all examples.
Write-Host "--- Formatting examples..."
Get-ChildItem -Path examples -Filter *.ash -Recurse | ForEach-Object {
    & $ashesCli fmt $_.FullName -w
    if ($LASTEXITCODE -ne 0) { throw "fmt failed: $($_.FullName)" }
}

### Format all tests.
Write-Host "--- Formatting tests..."
Get-ChildItem -Path tests -Filter *.ash -Recurse | ForEach-Object {
    if (Select-String -Path $_.FullName -Pattern '^\s*//\s*fmt-skip:' -Quiet) {
        return
    }
    & $ashesCli fmt $_.FullName -w
    if ($LASTEXITCODE -ne 0) { throw "fmt failed: $($_.FullName)" }
}

### Run examples.
Write-Host "--- Running examples..."
Get-ChildItem -Path examples -Filter *.ash -Depth 0 | ForEach-Object {
    & $ashesCli run $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "run failed: $($_.FullName)" }
}

### Run example projects.
Write-Host "--- Running example projects..."
Get-ChildItem -Path examples -Filter *.json -Recurse | ForEach-Object {
    Push-Location $_.DirectoryName
    try {
        & $ashesCli run --project $_.Name
        if ($LASTEXITCODE -ne 0) { throw "run --project failed: $($_.FullName)" }
    }
    finally {
        Pop-Location
    }
}

### Run tests.
Write-Host "--- Running tests..."
$testFiles = (Get-ChildItem -Path tests -Filter *.ash -Depth 0).FullName
& $ashesCli test @testFiles
if ($LASTEXITCODE -ne 0) { throw "test failed" }

### Run test projects.
Write-Host "--- Running test projects..."
Get-ChildItem -Path tests -Filter *.json -Recurse | ForEach-Object {
    Push-Location $_.DirectoryName
    try {
        & $ashesCli test --project $_.Name
        if ($LASTEXITCODE -ne 0) { throw "test --project failed: $($_.FullName)" }
    }
    finally {
        Pop-Location
    }
}

## Verify the VS Code extension can be built and packaged.
Write-Host "--- Verifying VS Code extension..."
Set-Location (Join-Path $repoRoot "vscode-extension")

$pnpmCmd = "pnpm"

Invoke-Step "Installing extension dependencies" {
    & $pnpmCmd install --frozen-lockfile --force
}

Invoke-Step "Linting extension" {
    & $pnpmCmd run lint
}

Invoke-Step "Checking extension formatting" {
    & $pnpmCmd run format:check
}

Invoke-Step "Compiling extension" {
    & $pnpmCmd run compile
}

Invoke-Step "Packaging VSIX" {
    & $pnpmCmd dlx '--config.ignoredBuiltDependencies[]=@vscode/vsce-sign' '--config.ignoredBuiltDependencies[]=keytar' @vscode/vsce@3.7.1 package --no-dependencies --allow-missing-repository --skip-license --out ../ashes-vscode.vsix
}
