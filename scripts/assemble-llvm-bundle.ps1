# assemble-llvm-bundle.ps1
# Copies only the required LLVM binaries from llvm-material/ into lib/Ashes/{win,linux}-x64/.
# Preserves the bin/ + lib/clang/ directory structure that clang expects.
# Run once; commit or package the output.

param(
    [string]$WinSource   = "$PSScriptRoot/../llvm-material/clang+llvm-22.1.1-x86_64-pc-windows-msvc",
    [string]$LinuxSource = "$PSScriptRoot/../llvm-material/LLVM-22.1.1-Linux-X64",
    [string]$OutRoot     = "$PSScriptRoot/../lib/Ashes"
)

$ErrorActionPreference = 'Stop'

function Copy-File([string]$src, [string]$dst) {
    $dir = Split-Path $dst -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    Copy-Item -Path $src -Destination $dst -Force
    $size = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Write-Host "  $dst ($size MB)"
}

# ── Windows x64 ──────────────────────────────────────────────────────────────
$winOut = Join-Path $OutRoot 'win-x64'

if (Test-Path $WinSource) {
    Write-Host ""
    Write-Host "=== Windows x64 ==="

    # Binaries
    $winBinaries = @(
        'clang.exe',      # .ll -> .obj compilation
        'lld-link.exe',   # PE linking
        'lli.exe',        # JIT interpreter (REPL)
        'LLVM-C.dll',     # runtime dependency for clang/lld/lli
        'LTO.dll',        # runtime dependency for lld-link
        'Remarks.dll'     # runtime dependency for lld-link
    )

    foreach ($bin in $winBinaries) {
        $src = Join-Path $WinSource "bin/$bin"
        if (Test-Path $src) {
            Copy-File $src (Join-Path $winOut "bin/$bin")
        } else {
            Write-Warning "Not found: $src"
        }
    }

    # clang resource dir (builtins for compiler-rt if needed)
    $rtBuiltins = Join-Path $WinSource 'lib/clang/22/lib/windows/clang_rt.builtins-x86_64.lib'
    if (Test-Path $rtBuiltins) {
        Copy-File $rtBuiltins (Join-Path $winOut 'lib/clang/22/lib/windows/clang_rt.builtins-x86_64.lib')
    }

    Write-Host ""
    Write-Host "  Import libs already in: $winOut"
    Write-Host "    kernel32.lib, shell32.lib, ws2_32.lib"
}
else {
    Write-Host "Skipping Windows: source not found at $WinSource"
}

# ── Linux x64 ────────────────────────────────────────────────────────────────
$linuxOut = Join-Path $OutRoot 'linux-x64'

if (Test-Path $LinuxSource) {
    Write-Host ""
    Write-Host "=== Linux x64 ==="

    $linuxBinaries = @(
        'clang-22',       # .ll -> .o compilation
        'lld',            # ELF linking (invoked as ld.lld)
        'lli'             # JIT interpreter (REPL)
    )

    foreach ($bin in $linuxBinaries) {
        $src = Join-Path $LinuxSource "bin/$bin"
        if (Test-Path $src) {
            Copy-File $src (Join-Path $linuxOut "bin/$bin")
        } else {
            Write-Warning "Not found: $src"
        }
    }

    # clang resource dir (builtins)
    $rtBuiltins = Join-Path $LinuxSource 'lib/clang/22/lib/x86_64-unknown-linux-gnu/libclang_rt.builtins.a'
    if (Test-Path $rtBuiltins) {
        Copy-File $rtBuiltins (Join-Path $linuxOut 'lib/clang/22/lib/x86_64-unknown-linux-gnu/libclang_rt.builtins.a')
    }

    # crt begin/end (needed for -nostartfiles linking)
    foreach ($crt in @('clang_rt.crtbegin.o', 'clang_rt.crtend.o')) {
        $src = Join-Path $LinuxSource "lib/clang/22/lib/x86_64-unknown-linux-gnu/$crt"
        if (Test-Path $src) {
            Copy-File $src (Join-Path $linuxOut "lib/clang/22/lib/x86_64-unknown-linux-gnu/$crt")
        }
    }
}
else {
    Write-Host "Skipping Linux: source not found at $LinuxSource"
}

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Bundle summary ==="

function Show-DirSize([string]$path, [string]$label) {
    if (Test-Path $path) {
        $total = (Get-ChildItem $path -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $mb = [math]::Round($total / 1MB, 1)
        Write-Host "  $label : $mb MB"
    }
}

Show-DirSize $winOut   "win-x64  "
Show-DirSize $linuxOut "linux-x64"