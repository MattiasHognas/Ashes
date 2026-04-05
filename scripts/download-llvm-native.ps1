# download-llvm-native.ps1
# Provisions LLVM native libraries for Ashes development on Windows.
#
# Windows libLLVM.dll:
#   Downloads LLVM-C.dll from the official LLVM GitHub release and renames it
#   to libLLVM.dll to match the DllImport name.
#
# Linux libLLVM.so (via -Linux or -All switch):
#   Invokes the bash script inside WSL, which runs `apt install libllvm<major>`
#   and copies the .so into runtimes/linux-{x64,arm64}/libLLVM.so so that dotnet
#   on Windows can include it in cross-platform builds.
#
# Usage:
#   .\scripts\download-llvm-native.ps1                       # Windows DLL only
#   .\scripts\download-llvm-native.ps1 -Linux                # Windows DLL + native-arch Linux .so via WSL
#   .\scripts\download-llvm-native.ps1 -All                  # all three: win-x64 + linux-x64 + linux-arm64
#   .\scripts\download-llvm-native.ps1 -LlvmVersion 22.1.3   # specify a different version
#   .\scripts\download-llvm-native.ps1 -LlvmMajor 23         # specify major for apt
#
# Prerequisites:
#   Windows DLL: tar (ships with Windows 10+)
#   Linux .so:   WSL with Ubuntu installed

param(
    [string]$LlvmVersion = "22.1.2",
    [string]$LlvmMajor,
    [switch]$Linux,
    [switch]$All
)

$ErrorActionPreference = 'Stop'

if (-not $LlvmMajor) {
    $LlvmMajor = $LlvmVersion.Split('.')[0]
}

# -All implies -Linux
if ($All) { $Linux = $true }

$ScriptDir = $PSScriptRoot
$RepoRoot  = (Resolve-Path "$ScriptDir/..").Path
$LibDir    = Join-Path $RepoRoot 'runtimes'
$TmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "ashes-llvm-$([Guid]::NewGuid().ToString('N').Substring(0,8))"

New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null

$WinUrl = "https://github.com/llvm/llvm-project/releases/download/llvmorg-$LlvmVersion/clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc.tar.xz"
$WinOut = Join-Path $LibDir 'win-x64'

New-Item -ItemType Directory -Force -Path $WinOut | Out-Null

try {
    # ── Windows DLL ──────────────────────────────────────────────────────────
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

    # ── Linux .so via WSL ────────────────────────────────────────────────────
    if ($Linux) {
        Write-Host ""
        Write-Host "=== Provisioning Linux libLLVM.so via WSL ==="

        # Check that WSL is available
        $wslCheck = Get-Command wsl -ErrorAction SilentlyContinue
        if (-not $wslCheck) {
            throw "WSL is not installed. Install WSL with Ubuntu to provision the Linux .so.`nSee: https://learn.microsoft.com/en-us/windows/wsl/install"
        }

        # Convert the repo-root Windows path to a WSL path
        $wslRepoRoot = wsl wslpath -u ($RepoRoot -replace '\\','/')
        $wslScript = "$wslRepoRoot/scripts/download-llvm-native.sh"

        if ($All) {
            # Download both linux-x64 and linux-arm64 via WSL
            Write-Host "Running: wsl bash $wslScript $LlvmMajor (native arch)"
            wsl bash $wslScript $LlvmMajor

            # Determine WSL's native arch to know which is the cross-arch
            $wslArch = wsl uname -m
            if ($wslArch -match 'x86_64|amd64') {
                $crossArch = "arm64"
            } else {
                $crossArch = "x64"
            }

            Write-Host ""
            Write-Host "Running: wsl bash $wslScript $LlvmMajor $crossArch (cross-download)"
            wsl bash $wslScript $LlvmMajor $crossArch
        } else {
            Write-Host "Running: wsl bash $wslScript $LlvmMajor"
            wsl bash $wslScript $LlvmMajor
        }

        # The bash script auto-detects the WSL architecture and writes to
        # runtimes/linux-x64/ or runtimes/linux-arm64/ accordingly.
        $foundLinuxSo = $false
        foreach ($rid in @("linux-x64", "linux-arm64")) {
            $linuxOut = Join-Path $LibDir $rid
            $soPath = Join-Path $linuxOut 'libLLVM.so'
            if (Test-Path $soPath) {
                $foundLinuxSo = $true
                $soSize = [math]::Round((Get-Item $soPath).Length / 1MB, 1)
                Write-Host "  -> $soPath ($soSize MB)"
            }
        }
        if (-not $foundLinuxSo) {
            throw "WSL completed, but no Linux libLLVM.so was found in either '$LibDir/linux-x64/libLLVM.so' or '$LibDir/linux-arm64/libLLVM.so'. Check the output from scripts/download-llvm-native.sh and verify it copied the library to the expected runtime folder."
        }

        if ($All) {
            # Verify both were downloaded
            $missingRids = @()
            foreach ($rid in @("linux-x64", "linux-arm64")) {
                $soPath = Join-Path (Join-Path $LibDir $rid) 'libLLVM.so'
                if (-not (Test-Path $soPath)) {
                    $missingRids += $rid
                }
            }
            if ($missingRids.Count -gt 0) {
                throw "WSL completed, but libLLVM.so is missing for: $($missingRids -join ', '). When using -All, both linux-x64 and linux-arm64 are expected."
            }
        }
    }

    # ── Summary ──────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Done (LLVM $LlvmVersion) ==="
    Write-Host "Native libraries installed into:"
    Write-Host "  $WinOut/libLLVM.dll"
    if ($All) {
        Write-Host "  $LibDir/linux-x64/libLLVM.so"
        Write-Host "  $LibDir/linux-arm64/libLLVM.so"
    } elseif ($Linux) {
        Write-Host "  Linux .so provisioned via WSL (see output above)"
    }
    Write-Host ""
    Write-Host "These are copied to the build output by Ashes.Backend.csproj."
    if (-not $Linux -and -not $All) {
        Write-Host ""
        Write-Host "TIP: To also provision the Linux .so for cross-builds, run:"
        Write-Host "  .\scripts\download-llvm-native.ps1 -All"
    }
}
finally {
    Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue
}
