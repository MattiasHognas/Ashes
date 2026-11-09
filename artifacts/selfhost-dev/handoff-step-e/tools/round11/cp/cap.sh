#!/bin/bash
# usage: cap.sh <MemoryMax> <command...> : runs the command in its own systemd scope with the memory cap and no swap
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
m=$1; shift
exec systemd-run --user --scope -q -p MemoryMax=$m -p MemorySwapMax=0 -E UseSharedCompilation=false -E MSBUILDDISABLENODEREUSE=1 -E DOTNET_CLI_USE_MSBUILD_SERVER=0 "$@"
