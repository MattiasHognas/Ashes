#!/bin/bash
# usage: challenge_ab.sh <labelA> <cliA> <labelB> <cliB> [challenge...]
# Compiles every compute-bound challenge at -O2 with two published compilers and runs each binary three
# times on the README's standard workload, one at a time under a 40G cgroup. Prints best wall time, peak
# RSS, exit status and the md5 of stdout per compiler, so a regression shows as a row whose two halves differ.
. "$(dirname "$0")/env.sh"
W=$J/chal
la=$1; ca=$2; lb=$3; cb=$4; shift 4
[ $# -eq 0 ] && set -- pidigits binary-trees mandelbrot fannkuch-redux n-body spectral-norm fasta reverse-complement k-nucleotide regex-redux
mkdir -p "$W"

args_for() {
    case $1 in
        pidigits) echo 10000 ;; binary-trees) echo 21 ;; mandelbrot) echo 16000 ;; fannkuch-redux) echo 11 ;;
        n-body) echo 50000000 ;; spectral-norm) echo 5500 ;; fasta) echo 25000000 ;; *) echo "" ;;
    esac
}
stdin_for() {
    case $1 in
        reverse-complement) echo "$W/fasta25m.txt" ;; k-nucleotide) echo "$W/fasta1m.txt" ;;
        regex-redux) echo "$W/fasta5m.txt" ;; *) echo /dev/null ;;
    esac
}
build() {
    "$2" compile "$R/challenges/$1/$1.ash" -o "$W/$1.$3" -O2 > "$W/$1.$3.build" 2>&1
    echo $?
}
fixture() {
    [ -s "$W/fasta$1.txt" ] && return
    [ -x "$W/fasta.$la" ] || build fasta "$ca" "$la" > /dev/null
    "$W/fasta.$la" "$2" > "$W/fasta$1.txt"
}
measure() {
    local name=$1 label=$2 best="" peak=0 code=0 sum=""
    for run in 1 2 3; do
        systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 \
            /usr/bin/time -f '%e %M' -o "$W/time.txt" "$W/$name.$label" $(args_for "$name") < "$(stdin_for "$name")" > "$W/$name.$label.out" 2> "$W/$name.$label.err"
        code=$?
        read -r secs kb < <(tail -1 "$W/time.txt")
        best=$(awk -v a="${best:-999999}" -v b="$secs" 'BEGIN{print (b<a)?b:a}')
        [ "$kb" -gt "$peak" ] && peak=$kb
        sum=$(md5sum < "$W/$name.$label.out" | cut -c1-8)
        [ "$code" -ne 0 ] && break
    done
    printf '%-7s exit=%-3s %8ss %8s MB  md5=%s' "$label" "$code" "$best" "$(awk -v k="$peak" 'BEGIN{printf "%.1f", k/1024}')" "$sum"
}

fixture 25m 25000000; fixture 1m 1000000; fixture 5m 5000000
for name in "$@"; do
    ba=$(build "$name" "$ca" "$la"); bb=$(build "$name" "$cb" "$lb")
    if [ "$ba" -ne 0 ] || [ "$bb" -ne 0 ]; then
        printf '%-19s COMPILE FAILED %s=%s %s=%s\n' "$name" "$la" "$ba" "$lb" "$bb"; continue
    fi
    printf '%-19s %s | %s\n' "$name" "$(measure "$name" "$la")" "$(measure "$name" "$lb")"
done
