#!/usr/bin/env bash
# Fetch the 1BRC measurements file into challenges/, optionally subsetting it to
# a smaller row count so the Ashes program (challenges/brc) can run to completion.
#
# Usage:
#   bash challenges/download.sh [URL]
#   ROWS=1000000 bash challenges/download.sh [URL]
#
#   URL   Override the download source (defaults to MEASUREMENTS_URL below).
#   ROWS  If set, write challenges/measurements.txt with only the first ROWS
#         lines (the full download is kept as challenges/measurements.full.txt).
#         If unset, measurements.txt is the full file.
#
# The full 1e9-row file is ~13 GB and will NOT complete with challenges/brc --
# see challenges/FLAWS.md. Use ROWS to pick a size that finishes; crank it up to
# reproduce the out-of-memory failure.
set -euo pipefail

# Default source: the upstream 1BRC station list (`Station;Latitude`). It is
# structurally identical to a measurements file (`Station;Number`), so brc.ash
# runs on it directly -- treating each latitude as the "temperature". It is small
# (~44k rows), good for a quick end-to-end run. Pass a real measurements URL as the
# first argument to override.
MEASUREMENTS_URL="https://github.com/gunnarmorling/1brc/raw/refs/heads/main/data/weather_stations.csv"

URL="${1:-$MEASUREMENTS_URL}"
ROWS="${ROWS:-}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
full="$here/measurements.full.txt"
out="$here/measurements.txt"

if [[ "$URL" == "<USER-PROVIDED-URL>" ]]; then
    echo "error: no download URL set." >&2
    echo "  Edit MEASUREMENTS_URL in $0, or run: bash $0 <url>" >&2
    exit 1
fi

echo "Downloading measurements from: $URL"
curl -fL --progress-bar -o "$full" "$URL"

if [[ -n "$ROWS" ]]; then
    echo "Subsetting first $ROWS rows -> $out"
    head -n "$ROWS" "$full" >"$out"
else
    echo "Using full file -> $out"
    cp -f "$full" "$out"
fi

lines="$(wc -l <"$out" | tr -d ' ')"
echo "Ready: $out ($lines rows)"
echo
echo "Build:  dotnet run --project src/Ashes.Cli -- compile challenges/brc.ash -o challenges/brc"
echo "Run:    ./challenges/brc $out"
