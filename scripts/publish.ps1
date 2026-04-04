# Publish self-contained Ashes compiler executables for win-x64 and linux-x64.
# Outputs: dist\win-x64\ashes.exe  and  dist\linux-x64\ashes
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CliProject = Join-Path $RepoRoot "src\Ashes.Cli\Ashes.Cli.csproj"
$LibSource = Join-Path $RepoRoot "lib"

# Version to embed into the published binaries.
# Can be overridden by passing -Version parameter, e.g.: .\publish.ps1 -Version 1.2.3
param(
    [string]$Version = if ($env:VERSION) { $env:VERSION } else { "0.0.0-dev" }
)

Write-Host "Restoring..."
dotnet restore $CliProject

foreach ($RID in @("win-x64", "linux-x64")) {
    Write-Host "Publishing $RID (version $Version)..."
    $OutputDir = Join-Path $RepoRoot "dist\$RID"
    dotnet publish $CliProject `
        --configuration Release `
        --runtime $RID `
        --self-contained true `
        "-p:PublishSingleFile=true" `
        "-p:Version=$Version" `
        --output $OutputDir `
        --no-restore

    if (Test-Path (Join-Path $OutputDir "lib")) {
        Remove-Item (Join-Path $OutputDir "lib") -Recurse -Force
    }

    # Copy standard library .ash files but exclude native LLVM binaries
    # (the self-contained publish already includes libLLVM in the app root).
    $DestLib = Join-Path $OutputDir "lib"
    robocopy $LibSource $DestLib /E /XD linux-x64 win-x64 /NFL /NDL /NJH /NJS /NC /NS /NP
    # robocopy returns 0-7 on success; only >=8 indicates failure.
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
    $LASTEXITCODE = 0
}

Write-Host "Done."
Write-Host "  dist\win-x64\ashes.exe"
Write-Host "  dist\linux-x64\ashes"
