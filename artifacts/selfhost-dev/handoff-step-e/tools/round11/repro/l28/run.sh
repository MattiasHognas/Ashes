# run.sh <name> : stage 0 live counts at ROUNDS (default 1 2 10); p0 plain, p1 compiled with ASHES_RC_POISON=1
D=/home/mattias/Documents/source/Ashes/.claude/worktrees/exit-40-0/work/l28; W=/home/mattias/Documents/source/Ashes/.claude/worktrees/exit-40-0
cd "$D"
for r in ${ROUNDS:-1 2 10}; do
  sed "s/ROUNDS/$r/" "$1.tpl" > "$1_$r.ash"
  for p in 0 1; do
    if [ $p = 1 ]; then pe="ASHES_RC_POISON=1"; else pe="X=1"; fi
    (cd $W && env $SW $pe ASHES_RC_LEAKCHECK=1 systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$D/$1_$r.ash" -o "$D/$1_$r.s$p" > "$D/$1_$r.s${p}log" 2>&1) || { echo "$1 r$r p$p compile fail: $(grep -m3 -E 'ASH|rror' $D/$1_$r.s${p}log | tr '\n' ' ' | cut -c1-300)"; continue; }
    out=$(systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 "$D/$1_$r.s$p" 2>&1 | tr '\n' ' ')
    echo "$1 r$r p$p: $out"
  done
done
