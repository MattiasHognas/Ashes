# generate-import-libs.ps1
# Generates minimal Windows import .lib files from .def files using llvm-dlltool.
# Run once; commit the output .lib files to the bundle.

param(
    [string]$LlvmBin = "$PSScriptRoot/../llvm-material/clang+llvm-22.1.1-x86_64-pc-windows-msvc/bin",
    [string]$OutDir  = "$PSScriptRoot/../lib/Ashes/win-x64"
)

$ErrorActionPreference = 'Stop'

$dlltool = Join-Path $LlvmBin 'llvm-dlltool.exe'
if (-not (Test-Path $dlltool)) {
    Write-Error "llvm-dlltool.exe not found at: $dlltool"
    exit 1
}

# Create output directory
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Create a temp directory for .def files
$defDir = Join-Path ([System.IO.Path]::GetTempPath()) "ashes-defs-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $defDir | Out-Null

try {
    # --- kernel32.def ---
    @"
LIBRARY kernel32.dll
EXPORTS
    ExitProcess
    GetStdHandle
    WriteFile
    ReadFile
    GetCommandLineW
    WideCharToMultiByte
    LocalFree
    CreateFileA
    CloseHandle
    GetFileSizeEx
    GetFileAttributesA
"@ | Set-Content (Join-Path $defDir 'kernel32.def') -Encoding ASCII

    # --- shell32.def ---
    @"
LIBRARY shell32.dll
EXPORTS
    CommandLineToArgvW
"@ | Set-Content (Join-Path $defDir 'shell32.def') -Encoding ASCII

    # --- ws2_32.def ---
    @"
LIBRARY ws2_32.dll
EXPORTS
    WSAStartup
    socket
    connect
    send
    recv
    closesocket
    inet_addr
    gethostbyname
"@ | Set-Content (Join-Path $defDir 'ws2_32.def') -Encoding ASCII

    # Generate import libs
    foreach ($name in @('kernel32', 'shell32', 'ws2_32')) {
        $def = Join-Path $defDir "$name.def"
        $lib = Join-Path $OutDir "$name.lib"

        Write-Host "Generating $name.lib ..."
        & $dlltool -m i386:x86-64 -d $def -l $lib

        if ($LASTEXITCODE -ne 0) {
            Write-Error "llvm-dlltool failed for $name"
            exit 1
        }

        $size = (Get-Item $lib).Length
        Write-Host "  -> $lib ($size bytes)"
    }

    Write-Host ""
    Write-Host "Done. Generated import libs in: $OutDir"
    Write-Host "Commit these .lib files to your bundle."
}
finally {
    Remove-Item -Recurse -Force $defDir -ErrorAction SilentlyContinue
}