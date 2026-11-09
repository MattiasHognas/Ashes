#!/bin/bash
# usage: heavy2.sh <command...> ; runs a heavy Ashes job (stage-1 build, suites, parity, probe, census) in one of
# three machine-wide slots, waiting until a slot is free. Slot 1 is the old single lock, so a plain
# `flock ashes-heavy.lock ...` still takes part and counts as one of the three.
D=/home/mattias/.claude/jobs
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
while true; do
    for slot in ashes-heavy.lock ashes-heavy-2.lock ashes-heavy-3.lock; do
        exec {fd}>"$D/$slot"
        if flock -n "$fd"; then
            "$@"
            code=$?
            flock -u "$fd"
            exit $code
        fi
        exec {fd}>&-
    done
    sleep 5
done
