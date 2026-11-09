#!/bin/bash
# every GRC_NO_* switch on a at 6 rounds (baseline 13 live); prints the switches that change the count
W=/home/mattias/Documents/source/Ashes/.claude/worktrees/exit-40-0
cd $W
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
sed "s/ROUNDS/6/g" work/l28/a.tpl > work/l28/scan.ash
grep -rhoE 'GRC_NO_[A-Z0-9_]+' src/Ashes.Semantics src/Ashes.Backend | sort -u > work/l28/switches.txt
wc -l < work/l28/switches.txt
while read sw; do
  rm -f work/l28/scan.bin
  env $sw=1 ASHES_RC_LEAKCHECK=1 systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile work/l28/scan.ash -o work/l28/scan.bin > /dev/null 2>&1 || { echo "$sw compile-fail"; continue; }
  out=$(systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 60 work/l28/scan.bin 2>&1 | tr '\n' ' ')
  case "$out" in *"ashes-rc-live 13 "*) ;; *) echo "$sw: $out";; esac
done < work/l28/switches.txt
echo SCANDONE
