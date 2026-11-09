# usage: lk.sh <tpl> [ENV=val...] ; stage-0 live cells at 1 and 2 rounds (leak check), prints "a b"
cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-post-guards
t=$1; shift; out=""
for r in 1 2; do
  sed "s/ROUNDS/$r/" $t > work/w9c/lk-$$-$r.ash; rm -f work/w9c/lk-$$-$r.bin
  env "$@" ASHES_RC_LEAKCHECK=1 UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet src/Ashes.Cli/bin/Release/net10.0/ashes.dll compile work/w9c/lk-$$-$r.ash -o work/w9c/lk-$$-$r.bin >/dev/null 2>&1
  out="$out $(timeout 30 work/w9c/lk-$$-$r.bin 2>&1 >/dev/null | awk '/ashes-rc-live/{print $2}')"
done
echo $out
