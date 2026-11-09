#!/bin/bash
# usage: memwatch.sh ; samples memory every 2 s into work/memwatch.log and kills a runaway job before RAM
# and swap fill. A process's footprint is VmRSS + VmSwap, so a job that is mostly swapped out still counts.
# Candidates are only processes in the Claude app scope or a systemd-run job scope (cgroup path
# app-com.anthropic.Claude-* or run-*), whatever their name: the desktop and the user's own applications
# are never touched, and neither is the Claude app itself (comm claude/Claude/electron). The 2026-10-25
# OOMs came from a small compiled reproducer (comm t11_1) inside the Claude scope that a name filter missed.
# It kills the largest candidate when any of these holds:
#   - MemAvailable falls below FLOOR_GB (default 10)
#   - swap in use exceeds SWAP_GB (default 6) while MemAvailable is under 20 GB (idle swap is harmless)
#   - one candidate's footprint exceeds PROC_GB (default 24); the largest legitimate job, a stage-1
#     build, peaks near 15 GB
# After a kill it waits 10 s for memory to settle before judging again.
# Run it outside the Claude scope so it survives the Claude process group:
#   systemd-run --user --unit=ashes-memwatch bash <this file>
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
LOG=$R/artifacts/selfhost-dev/handoff-step-e/work/memwatch.log
FLOOR_KB=$(( ${FLOOR_GB:-10} * 1024 * 1024 ))
SWAP_KB=$(( ${SWAP_GB:-6} * 1024 * 1024 ))
PRESSURE_KB=$(( 20 * 1024 * 1024 ))
PROC_KB=$(( ${PROC_GB:-24} * 1024 * 1024 ))
ME=$(id -u)
PEAK_KB=0

largest() {
    for d in /proc/[0-9]*; do
        [ -O "$d" ] || continue
        cg=$(cat "$d/cgroup" 2>/dev/null)
        case "$cg" in
            *app-com.anthropic.Claude-*|*/run-*) ;;
            *) continue ;;
        esac
        awk '
            /^Name:/ { name = $2 }
            /^Pid:/ { pid = $2 }
            /^VmRSS:/ { rss = $2 }
            /^VmSwap:/ { swp = $2 }
            END { if (rss + swp > 0) print rss + swp, pid, name }' "$d/status" 2>/dev/null
    done | grep -v -E ' (claude|Claude|electron|chrome_crashpad|bash|sh|systemd-run|sleep)$' | sort -rn | head -1
}

echo "$(date +%T) memwatch start floor=${FLOOR_GB:-10}G swap=${SWAP_GB:-6}G(under 20G avail) proc=${PROC_GB:-24}G scopes=Claude,run-*" >> "$LOG"
while true; do
    AVAIL_KB=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
    SWAP_USED_KB=$(awk '/^SwapTotal:/ { t = $2 } /^SwapFree:/ { f = $2 } END { print t - f }' /proc/meminfo)
    TOP=$(largest)
    TOP_KB=$(echo "$TOP" | cut -d' ' -f1)
    TOP_PID=$(echo "$TOP" | cut -d' ' -f2)
    TOP_NAME=$(echo "$TOP" | cut -d' ' -f3)
    if [ -n "$TOP_KB" ] && [ "$TOP_KB" -gt "$PEAK_KB" ] && [ "$TOP_KB" -gt 1048576 ]; then
        PEAK_KB=$TOP_KB
        echo "$(date +%T) new peak $((TOP_KB / 1024)) MB avail $((AVAIL_KB / 1024)) MB swap $((SWAP_USED_KB / 1024)) MB: pid $TOP_PID $TOP_NAME $(tr '\0' ' ' < "/proc/$TOP_PID/cmdline" 2>/dev/null | cut -c1-160)" >> "$LOG"
    fi
    REASON=""
    if [ "$AVAIL_KB" -lt "$FLOOR_KB" ]; then REASON="avail $((AVAIL_KB / 1024)) MB"; fi
    if [ "$SWAP_USED_KB" -gt "$SWAP_KB" ] && [ "$AVAIL_KB" -lt "$PRESSURE_KB" ]; then REASON="$REASON swap $((SWAP_USED_KB / 1024)) MB"; fi
    if [ -n "$TOP_KB" ] && [ "$TOP_KB" -gt "$PROC_KB" ]; then REASON="$REASON footprint $((TOP_KB / 1024)) MB"; fi
    if [ -n "$REASON" ] && [ -n "$TOP_PID" ] && [ "$TOP_KB" -gt 1048576 ]; then
        echo "$(date +%T) KILL ($REASON) pid $TOP_PID $TOP_NAME footprint $((TOP_KB / 1024)) MB: $(tr '\0' ' ' < "/proc/$TOP_PID/cmdline" 2>/dev/null | cut -c1-200)" >> "$LOG"
        kill -9 "$TOP_PID"
        sleep 10
    fi
    sleep 2
done
