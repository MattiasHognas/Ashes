#!/bin/bash
# usage: challengesvsmain.sh <label> ; the landing gate's last step in one go: publishes a compiler from
# origin/main and one from this checkout, then runs every challenges/ program with both (challenge_ab.sh).
# A regression shows as a row whose two halves differ. Log: logs/chal-<label>.log.
. "$(dirname "$0")/env.sh"
label=$1
{
    bash "$HERE/publishref.sh" origin/main main || exit 1
    bash "$HERE/publishcli.sh" "$label" || exit 1
    bash "$HERE/challenge_ab.sh" main "$J/cli-main/ashes" "$label" "$J/cli-$label/ashes"
    echo "challenges done exit=$?"
} > "$T/chal-$label.log" 2>&1
tail -12 "$T/chal-$label.log" | cut -c1-160
