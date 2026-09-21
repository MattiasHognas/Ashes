#!/bin/bash
# usage: suitebt.sh <suite> [ENV=val...] ; compiles one selfhost suite with --debug (CLI must be built) and
# prints a compact backtrace at the point the test process exits, which for a failed assertion is the
# assertion itself.
. "$(dirname "$0")/env.sh"
suite=$1; shift
[ $# -eq 0 ] && set -- _UNUSED=1
cd "$R" || exit 1
rm -rf "selfhost/tests/$suite/out"
env "$@" dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --debug --project "selfhost/tests/$suite/ashes.json" > "$T/suitebt.build" 2>&1
bin=$(ls "selfhost/tests/$suite/out/" 2>/dev/null | head -1)
[ -z "$bin" ] && { echo "COMPILE FAILED"; tail -3 "$T/suitebt.build"; exit 1; }
bash -c "ulimit -s 1048576; gdb -q -batch -ex 'catch syscall exit exit_group' -ex run -ex 'bt ${FRAMES:-12}' --args 'selfhost/tests/$suite/out/$bin'" > "$T/suitebt.log" 2>&1
grep -E "^#[0-9]+ " "$T/suitebt.log" | sed -E 's/0x[0-9a-f]{12,16} in //; s/ \([^)]*\)//; s/ at .*\// @/' | cut -c1-150
