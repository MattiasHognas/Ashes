#!/usr/bin/env bash
# Fetch the 1BRC station seed list and (optionally) GENERATE a measurements file
# of arbitrary size from it, so the Ashes program (challenges/brc) can be run at
# anything from a quick 44k-row smoke test up to the real billion-row challenge.
#
# Usage:
#   bash challenges/download.sh [URL]
#   ROWS=1000000000 bash challenges/download.sh [URL]
#
#   URL   Override the seed source (defaults to STATIONS_URL below).
#   ROWS  If set, GENERATE challenges/measurements.txt with ROWS measurement rows
#         by sampling station names from the seed list and emitting a realistic
#         random temperature per row (1BRC-style: per-station mean + Gaussian
#         spread, one decimal, clamped to -99.9..99.9). The seed list is kept as
#         challenges/measurements.full.txt.
#         If unset, measurements.txt IS the seed list verbatim (~44k rows) -- a
#         quick end-to-end run, treating each station's latitude as its value.
#
# There is no URL anywhere that serves a billion-row file: the upstream 1BRC repo
# only checks in the ~44k-row station list (data/weather_stations.csv); the
# ~13 GB measurements.txt is meant to be generated locally from it. That is what
# ROWS does here. Crank ROWS up to 1e9 to reproduce the out-of-memory failure
# documented in challenges/FLAWS.md.
set -euo pipefail

# Default source: the upstream 1BRC station list (`Station;Latitude`). With ROWS
# unset it is structurally identical to a measurements file (`Station;Number`), so
# brc.ash runs on it directly -- treating each latitude as the "temperature". With
# ROWS set it is used purely as the pool of station names to sample from.
STATIONS_URL="https://github.com/gunnarmorling/1brc/raw/refs/heads/main/data/weather_stations.csv"

URL="${1:-$STATIONS_URL}"
ROWS="${ROWS:-}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
full="$here/measurements.full.txt"
out="$here/measurements.txt"

if [[ "$URL" == "<USER-PROVIDED-URL>" ]]; then
    echo "error: no download URL set." >&2
    echo "  Edit STATIONS_URL in $0, or run: bash $0 <url>" >&2
    exit 1
fi

echo "Downloading station seed list from: $URL"
curl -fL --progress-bar -o "$full" "$URL"

if [[ -n "$ROWS" ]]; then
    if ! [[ "$ROWS" =~ ^[0-9]+$ ]] || [[ "$ROWS" -le 0 ]]; then
        echo "error: ROWS must be a positive integer (got '$ROWS')." >&2
        exit 1
    fi
    echo "Generating $ROWS measurement rows -> $out"
    # Read the seed list (skip blank/comment lines, take the name before ';'),
    # assign each station a random mean temperature, then emit ROWS rows sampling
    # a station uniformly and drawing a Gaussian temperature around its mean.
    awk -v rows="$ROWS" '
        function gauss() { return sqrt(-2 * log(rand() + 1e-12)) * cos(6.2831853071795864 * rand()) }
        BEGIN { srand() }
        # Pass 1: collect station names from the seed file.
        {
            line = $0
            if (line == "" || substr(line, 1, 1) == "#") next
            p = index(line, ";")
            name = (p > 0) ? substr(line, 1, p - 1) : line
            if (name == "") next
            stations[n] = name
            mean[n] = -30 + rand() * 70   # per-station mean in [-30, 40)
            n++
        }
        END {
            if (n == 0) { print "error: no stations in seed list" > "/dev/stderr"; exit 1 }
            for (i = 0; i < rows; i++) {
                s = int(rand() * n)
                t = mean[s] + gauss() * 10.0
                if (t > 99.9) t = 99.9
                if (t < -99.9) t = -99.9
                printf "%s;%.1f\n", stations[s], t
                if ((i + 1) % 50000000 == 0) printf "  ... %d rows\n", i + 1 > "/dev/stderr"
            }
        }
    ' "$full" >"$out"
else
    echo "Using seed list verbatim -> $out"
    cp -f "$full" "$out"
fi

if [[ -n "$ROWS" ]]; then
    echo "Ready: $out ($ROWS rows)"
else
    lines="$(wc -l <"$out" | tr -d ' ')"
    echo "Ready: $out ($lines rows)"
fi
echo
echo "Build:  dotnet run --project src/Ashes.Cli -- compile challenges/brc.ash -o challenges/brc"
echo "Run:    ./challenges/brc $out"
