# download-llvm-native.ps1
# Downloads LLVM-C.dll from the official LLVM GitHub release for Windows
# development, and renames it to libLLVM.dll to match the DllImport name.
#
# For Linux, use scripts/download-llvm-native.sh which installs libLLVM via apt.
#
# Usage:
#   .\scripts\download-llvm-native.ps1                     # uses default version 22.1.2
#   .\scripts\download-llvm-native.ps1 -LlvmVersion 22.1.3 # specify a different version
#
# Prerequisites: tar (ships with Windows 10+)

param(
    [string]$LlvmVersion = "22.1.2"
)

$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
$RepoRoot  = (Resolve-Path "$ScriptDir/..").Path
$LibDir    = Join-Path $RepoRoot 'lib/Ashes'
$TmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "ashes-llvm-$([Guid]::NewGuid().ToString('N').Substring(0,8))"

New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null

$WinUrl = "https://github.com/llvm/llvm-project/releases/download/llvmorg-$LlvmVersion/clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc.tar.xz"
$WinOut = Join-Path $LibDir 'win-x64'

New-Item -ItemType Directory -Force -Path $WinOut | Out-Null

try {
    Write-Host ""
    Write-Host "=== Downloading LLVM $LlvmVersion Windows x64 (LLVM-C.dll) ==="
    $winArchive = Join-Path $TmpDir 'llvm-win.tar.xz'
    Invoke-WebRequest -Uri $WinUrl -OutFile $winArchive -UseBasicParsing

    Write-Host "Extracting LLVM-C.dll..."
    $winExtract = Join-Path $TmpDir 'win'
    New-Item -ItemType Directory -Force -Path $winExtract | Out-Null
    tar -xf $winArchive -C $winExtract

    $llvmCDll = Get-ChildItem -Path $winExtract -Recurse -Filter 'LLVM-C.dll' |
        Select-Object -First 1
    if (-not $llvmCDll) {
        throw "Could not find LLVM-C.dll in Windows archive"
    }

    # Rename LLVM-C.dll -> libLLVM.dll to match DllImport name ("libLLVM")
    Copy-Item -Path $llvmCDll.FullName -Destination (Join-Path $WinOut 'libLLVM.dll') -Force
    $size = [math]::Round((Get-Item (Join-Path $WinOut 'libLLVM.dll')).Length / 1MB, 1)
    Write-Host "  -> $WinOut/libLLVM.dll ($size MB)"

    Write-Host ""
    Write-Host "=== Done (LLVM $LlvmVersion) ==="
    Write-Host "Windows native library installed into:"
    Write-Host "  $WinOut/libLLVM.dll"
    Write-Host ""
    Write-Host "This is copied to the build output by Ashes.Backend.csproj."
    Write-Host ""
    Write-Host "NOTE: For Linux, run ./scripts/download-llvm-native.sh which"
    Write-Host "      installs libLLVM-$($LlvmVersion.Split('.')[0]).so via apt."
}
finally {
    Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue
}
