# download-llvm-native.ps1
# Downloads LLVM native libraries from official LLVM GitHub releases and places
# them into src/Ashes.Backend/runtimes/{rid}/native/ so the .csproj can copy
# them to the build output.
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

$LlvmMajor = $LlvmVersion.Split('.')[0]
$ScriptDir = $PSScriptRoot
$RepoRoot  = (Resolve-Path "$ScriptDir/..").Path
$RuntimesDir = Join-Path $RepoRoot 'src/Ashes.Backend/runtimes'
$TmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "ashes-llvm-$([Guid]::NewGuid().ToString('N').Substring(0,8))"

New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null

$LinuxUrl = "https://github.com/llvm/llvm-project/releases/download/llvmorg-$LlvmVersion/LLVM-$LlvmVersion-Linux-X64.tar.xz"
$WinUrl   = "https://github.com/llvm/llvm-project/releases/download/llvmorg-$LlvmVersion/clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc.tar.xz"

$LinuxOut = Join-Path $RuntimesDir 'linux-x64/native'
$WinOut   = Join-Path $RuntimesDir 'win-x64/native'

New-Item -ItemType Directory -Force -Path $LinuxOut | Out-Null
New-Item -ItemType Directory -Force -Path $WinOut   | Out-Null

try {
    # ── Linux x64 ────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Downloading LLVM $LlvmVersion Linux x64 ==="
    $linuxArchive = Join-Path $TmpDir 'llvm-linux.tar.xz'
    Invoke-WebRequest -Uri $LinuxUrl -OutFile $linuxArchive -UseBasicParsing

    Write-Host "Extracting libLLVM..."
    $linuxExtract = Join-Path $TmpDir 'linux'
    New-Item -ItemType Directory -Force -Path $linuxExtract | Out-Null
    tar -xf $linuxArchive -C $linuxExtract

    # Find the real shared library (prefer versioned name, fall back to .so.*)
    $libLlvm = Get-ChildItem -Path $linuxExtract -Recurse -Filter "libLLVM-$LlvmMajor.so" |
        Where-Object { -not $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) } |
        Select-Object -First 1
    if (-not $libLlvm) {
        $libLlvm = Get-ChildItem -Path $linuxExtract -Recurse -Filter "libLLVM.so.*" |
            Where-Object { -not $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) } |
            Select-Object -First 1
    }
    if (-not $libLlvm) {
        throw "Could not find libLLVM shared library in Linux archive"
    }

    Copy-Item -Path $libLlvm.FullName -Destination (Join-Path $LinuxOut 'libLLVM.so') -Force
    $size = [math]::Round((Get-Item (Join-Path $LinuxOut 'libLLVM.so')).Length / 1MB, 1)
    Write-Host "  -> $LinuxOut/libLLVM.so ($size MB)"

    # ── Windows x64 ──────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Downloading LLVM $LlvmVersion Windows x64 ==="
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

    Copy-Item -Path $llvmCDll.FullName -Destination (Join-Path $WinOut 'libLLVM.dll') -Force
    $size = [math]::Round((Get-Item (Join-Path $WinOut 'libLLVM.dll')).Length / 1MB, 1)
    Write-Host "  -> $WinOut/libLLVM.dll ($size MB)"

    # ── Summary ──────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Done (LLVM $LlvmVersion) ==="
    Write-Host "Native libraries installed into:"
    Write-Host "  $LinuxOut/libLLVM.so"
    Write-Host "  $WinOut/libLLVM.dll"
    Write-Host ""
    Write-Host "These will be copied to the build output by Ashes.Backend.csproj."
}
finally {
    Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue
}
