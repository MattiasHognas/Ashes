#!/bin/bash
# dlrun.sh <tag> : rebuild the stage-1 lowering tool (work/q41/dl) from the current semantics source, then the full tally of loops10/11 under it
R=$(cd "$(dirname "$(realpath "$0")")/../.." && pwd)
Q=$(dirname "$(realpath "$0")")
[ -x "$Q/rccells" ] || gcc -O2 -o "$Q/rccells" "$Q/rccells.c" || exit 1
sed -i -E "s#\"path\": \"[^\"]*/selfhost/packages/(semantics|frontend)\"#\"path\": \"$R/selfhost/packages/\\1\"#" "$Q/dl/ashes.json"
cd "$Q/dl" || exit 1
ASHES_RC_LEAKCHECK=1 dotnet run -c Release --no-build --project ../../../src/Ashes.Cli -- compile --project ashes.json -o dl-$1.bin > build-$1.log 2>&1 || { tail -3 build-$1.log; exit 1; }
cd "$R"; export LC_ALL=C; base=$((0x100000000000)); chunk=$((256 * 1048576)); W=$Q/run-$1; mkdir -p $W
for n in 10 11; do D=$W/dump-$n; mkdir -p $D
  { echo "set pagination off"; echo "catch syscall exit exit_group"; echo "run $R/artifacts/selfhost-dev/tiny/loops$n.ash $R/lib/Ashes > $W/out$n.txt"; for k in 0 1; do echo "dump binary memory $D/full-$k.bin $((base + k * chunk)) $((base + (k + 1) * chunk))"; done; echo "kill"; } > $W/g$n.gdb
  bash -c "ulimit -s 1048576; gdb -q -batch -x $W/g$n.gdb $Q/dl/dl-$1.bin" > $W/gdb$n.log 2>&1
  CELLS=1 $Q/rccells 0 0 $D/full-*.bin | awk '{print $3"_"$4}' | sort | uniq -c | awk '{print $2, $1}' | sort > $W/t$n.txt
done
join -a1 -a2 -e0 -o 0,1.2,2.2 $W/t10.txt $W/t11.txt | awk '{d=$3-$2; if (d!=0) print $1, $2, $3, d}' | sort -k4 -n -r > $W/diff.txt
echo "$1 NET $(( $(awk '{s+=$2} END{print s}' $W/t11.txt) - $(awk '{s+=$2} END{print s}' $W/t10.txt) )) GROWTH $(awk '$4>0{s+=$4} END{print s+0}' $W/diff.txt)"; head -6 $W/diff.txt
