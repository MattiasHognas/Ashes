#!/usr/bin/env bash
# Behavioural check: compiles every test program with stage 0 and with stage 1, runs both
# binaries the way the test's directives say, and compares compile outcome, stdout, exit code
# and the exit live-cell count. See README.md beside this script.
set -uo pipefail

usage() {
    cat <<'EOF'
Usage: bash selfhost/tests/behaviour/check.sh --stage0 <path> --stage1 <path>
           [--baseline <tsv>] [--out <dir>] [--cache <dir>] [--jobs N] [files...]

  --stage0 <path>    stage-0 compiler (the C# `ashes` executable)
  --stage1 <path>    stage-1 compiler (the self-hosted executable)
  --baseline <tsv>   compare with an earlier result; fail only on regressions
  --out <dir>        result directory (default work/behaviour/<timestamp>)
  --cache <dir>      stage-0 result cache (default work/behaviour/stage0-cache)
  --jobs N           programs checked in parallel (default 4)
  files...           test programs to check (default: every tests/*.ash)

Environment: BEHAVIOUR_STAGE0_MEM (8G), BEHAVIOUR_STAGE0_TIMEOUT (120),
BEHAVIOUR_STAGE1_MEM (4G), BEHAVIOUR_STAGE1_TIMEOUT (120), BEHAVIOUR_RUN_MEM (4G),
BEHAVIOUR_RUN_TIMEOUT (30), BEHAVIOUR_JOBS (4), BEHAVIOUR_KEEP_BINARIES (0).
EOF
}

script_path=$(realpath "${BASH_SOURCE[0]}")
repo_root=$(realpath "$(dirname "$script_path")/../../..")
cache_format=2
tab=$'\t'

# Runs a command under a memory cap and a timeout. The exit code is the command's own, 124 for
# a timeout, and 128+N for a death by signal N (137 covers the memory cap's SIGKILL).
capped() {
    local memory=$1 seconds=$2
    shift 2
    systemd-run --user --scope -q -p "MemoryMax=$memory" -p MemorySwapMax=0 \
        timeout -k 5 "$seconds" "$@"
}

# Names an abnormal exit code, or prints nothing for an ordinary one.
abnormal_exit() {
    local code=$1 memory=$2 seconds=$3
    if [[ $code -eq 124 ]]; then
        printf 'timeout (%ss)' "$seconds"
    elif [[ $code -eq 137 ]]; then
        printf 'killed (SIGKILL, memory cap %s)' "$memory"
    elif [[ $code -gt 128 && $code -le 192 ]]; then
        printf 'signal %s (SIG%s)' "$((code - 128))" "$(kill -l "$((code - 128))" 2>/dev/null || echo '?')"
    fi
}

# Keeps a detail on one TSV-safe line of bounded length.
clean_detail() {
    local text=$1
    text=${text//$tab/ }
    text=${text//$'\n'/ }
    text=${text//$'\r'/ }
    if [[ ${#text} -gt 300 ]]; then
        text="${text:0:300}..."
    fi
    printf '%s' "$text"
}

trim_trailing() {
    local text=$1
    printf '%s' "${text%"${text##*[![:space:]]}"}"
}

trim_both() {
    local text=$1
    text="${text#"${text%%[![:space:]]*}"}"
    printf '%s' "${text%"${text##*[![:space:]]}"}"
}

# Decodes the escapes the test runner accepts in `// stdin:` (\n, \r, \t, \\) into `decoded`.
# The result is a variable because a command substitution would drop trailing newlines.
decode_input() {
    local text=$1 result="" index=0 char next
    while [[ $index -lt ${#text} ]]; do
        char=${text:index:1}
        if [[ $char != "\\" || $index -eq $((${#text} - 1)) ]]; then
            result+=$char
        else
            index=$((index + 1))
            next=${text:index:1}
            case "$next" in
                n) result+=$'\n' ;;
                r) result+=$'\r' ;;
                t) result+=$'\t' ;;
                *) result+=$next ;;
            esac
        fi
        index=$((index + 1))
    done
    decoded=$result
}

# Reads the leading `//` directive block of a test file into the d_* variables.
parse_directives() {
    local file=$1 line trimmed text body path content lowered
    d_has_expected=0
    d_expected=""
    d_compile_error=0
    d_exit=0
    d_has_stdin=0
    d_stdin=""
    d_has_stderr=0
    d_stderr=""
    d_has_stderr_contains=0
    d_stderr_contains=""
    d_executable_directory=""
    d_working_directory=""
    d_skip=""
    d_fixture_paths=()
    d_fixture_kinds=()
    d_fixture_contents=()
    while IFS= read -r line || [[ -n $line ]]; do
        line=${line%$'\r'}
        trimmed=$(trim_both "$line")
        [[ -z $trimmed ]] && continue
        [[ $trimmed != //* ]] && break
        text=${line#*//}
        text="${text#"${text%%[![:space:]]*}"}"
        lowered=${text,,}
        case "$lowered" in
            expect:*)
                d_expected=$(trim_both "${text:7}")
                d_has_expected=1
                d_compile_error=0
                ;;
            expect-compile-error:*)
                d_expected=$(trim_both "${text:21}")
                d_has_expected=1
                d_compile_error=1
                d_exit=1
                ;;
            expect-stderr:*)
                d_stderr=$(trim_both "${text:14}")
                d_has_stderr=1
                ;;
            expect-stderr-contains:*)
                d_stderr_contains=$(trim_both "${text:23}")
                d_has_stderr_contains=1
                ;;
            exit:*)
                body=$(trim_both "${text:5}")
                [[ $body =~ ^-?[0-9]+$ ]] && d_exit=$body
                ;;
            stdin:*)
                decode_input "$(trim_both "${text:6}")"
                d_stdin=$decoded
                d_has_stdin=1
                ;;
            file:* | file-bytes:*)
                if [[ $lowered == file-bytes:* ]]; then
                    body=${text:11}
                    d_fixture_kinds+=(bytes)
                else
                    body=${text:5}
                    d_fixture_kinds+=(text)
                fi
                path=$(trim_both "${body%%=*}")
                content=${body#*=}
                content=${content# }
                if [[ $body != *=* || -z $path || $path == /* || $path == *..* ]]; then
                    d_skip="needs fixture (unsupported file directive)"
                fi
                d_fixture_paths+=("$path")
                d_fixture_contents+=("$content")
                ;;
            executable-directory:*)
                d_executable_directory=$(trim_both "${text:21}")
                ;;
            working-directory:*)
                d_working_directory=$(trim_both "${text:18}")
                ;;
            tcp-server:* | tcp-expect:* | tcp-send:*)
                d_skip="needs fixture (tcp loopback server)"
                ;;
            tls-*)
                d_skip="needs fixture (tls loopback server)"
                ;;
            skip-on:*)
                if [[ ",${lowered:8}," == *linux-x64* ]]; then
                    d_skip="skip-on (linux-x64)"
                fi
                ;;
        esac
    done < "$file"
    if [[ $d_executable_directory == /* || $d_executable_directory == *..* \
        || $d_working_directory == /* || $d_working_directory == *..* ]]; then
        d_skip="needs fixture (unsupported directory directive)"
    fi
    if [[ $d_has_expected -eq 0 ]]; then
        d_skip="no expectation (the test runner skips it too)"
    fi
}

# Builds the isolated fixture directory a program runs in.
materialize_fixture() {
    local root=$1 index destination hex bytes
    mkdir -p "$root"
    [[ -n $d_executable_directory ]] && mkdir -p "$root/$d_executable_directory"
    [[ -n $d_working_directory ]] && mkdir -p "$root/$d_working_directory"
    for index in "${!d_fixture_paths[@]}"; do
        destination="$root/${d_fixture_paths[index]}"
        mkdir -p "$(dirname "$destination")"
        if [[ ${d_fixture_kinds[index]} == bytes ]]; then
            bytes=""
            for hex in ${d_fixture_contents[index]}; do
                bytes+="\\x$hex"
            done
            printf '%b' "$bytes" > "$destination"
        else
            printf '%s' "${d_fixture_contents[index]}" > "$destination"
        fi
    done
}

# Runs a compiled program in a fresh fixture directory. Leaves stdout, stderr, run.rc and live
# in the result directory, with the fixture directory's own path replaced by <FIXTURE>.
run_program() {
    local binary=$1 result=$2 fixture escaped code
    fixture="$result/fixture"
    rm -rf "$fixture"
    materialize_fixture "$fixture"
    cp "$binary" "$fixture/$d_executable_directory/program"
    if [[ $d_has_stdin -eq 1 ]]; then
        printf '%s' "$d_stdin" > "$result/stdin"
    else
        : > "$result/stdin"
    fi
    (
        cd "$fixture/$d_working_directory" || exit 125
        capped "$BEHAVIOUR_RUN_MEM" "$BEHAVIOUR_RUN_TIMEOUT" "$fixture/$d_executable_directory/program" \
            < "$result/stdin" > "$result/stdout" 2> "$result/stderr"
    )
    code=$?
    printf '%s\n' "$code" > "$result/run.rc"
    if grep -qaF "$fixture" "$result/stdout" "$result/stderr"; then
        escaped=$(printf '%s' "$fixture" | sed 's/[][\.*^$|]/\\&/g')
        sed -i "s|$escaped|<FIXTURE>|g" "$result/stdout" "$result/stderr"
    fi
    awk '{ last = $0 } END { if (last ~ /^ashes-rc-live [0-9]+$/) { split(last, parts, " "); print parts[2] } else { print "none" } }' \
        "$result/stderr" > "$result/live"
    rm -rf "$fixture" "$result/stdin"
}

# Compiles with one compiler from the repository root. Leaves compile.log and compile.rc.
compile_program() {
    local stage=$1 compiler=$2 source=$3 binary=$4 result=$5 code
    if [[ $stage -eq 0 ]]; then
        (
            cd "$repo_root" || exit 125
            capped "$BEHAVIOUR_STAGE0_MEM" "$BEHAVIOUR_STAGE0_TIMEOUT" "$compiler" compile "$source" -o "$binary" \
                > "$result/compile.log" 2>&1 < /dev/null
        )
        code=$?
    else
        (
            cd "$repo_root" || exit 125
            ulimit -s 1048576
            capped "$BEHAVIOUR_STAGE1_MEM" "$BEHAVIOUR_STAGE1_TIMEOUT" "$compiler" compile "$source" -o "$binary" \
                > "$result/compile.log" 2>&1 < /dev/null
        )
        code=$?
    fi
    if [[ $code -eq 0 && ! -x $binary ]]; then
        code=1
        printf 'compiler exited 0 without writing an executable\n' >> "$result/compile.log"
    fi
    printf '%s\n' "$code" > "$result/compile.rc"
}

# The first line of a compile log that carries a message: the stage-1 compiler's own exit
# live-cell line and blank lines are not one.
first_error_line() {
    awk '/^ashes-rc-live [0-9]+$/ { next } /^[[:space:]]*$/ { next } { print; exit }' "$1"
}

stderr_without_live_line() {
    awk '/^ashes-rc-live [0-9]+$/ { next } { print }' "$1"
}

# Checks a result directory against the test's own expectation. Prints the mismatch, or nothing.
expectation_mismatch() {
    local result=$1 compile_code run_code actual abnormal
    compile_code=$(< "$result/compile.rc")
    if [[ $d_compile_error -eq 1 ]]; then
        if [[ $compile_code -eq 0 ]]; then
            printf 'accepts invalid program (expected a compile error)'
        elif [[ $compile_code -ne 1 ]]; then
            printf 'compile exit %s (expected 1)' "$compile_code"
        elif ! grep -qaF -- "$d_expected" "$result/compile.log"; then
            printf 'diagnostic lacks expected text (%s)' "$(first_error_line "$result/compile.log")"
        fi
        return
    fi
    if [[ $compile_code -ne 0 ]]; then
        abnormal=$(abnormal_exit "$compile_code" "$BEHAVIOUR_STAGE0_MEM" "$BEHAVIOUR_STAGE0_TIMEOUT")
        if [[ -n $abnormal ]]; then
            printf 'compile %s' "$abnormal"
        else
            printf 'compile failed (%s)' "$(first_error_line "$result/compile.log")"
        fi
        return
    fi
    run_code=$(< "$result/run.rc")
    actual=$(trim_trailing "$(cat "$result/stdout" 2>/dev/null | tr -d '\0')")
    if [[ $actual != "$(trim_trailing "$d_expected")" ]]; then
        printf 'stdout mismatch (expected "%s", got "%s", exit %s)' "$d_expected" "${actual:0:80}" "$run_code"
    elif [[ $run_code -ne $d_exit ]]; then
        printf 'exit mismatch (expected %s, got %s)' "$d_exit" "$run_code"
    elif [[ $d_has_stderr -eq 1 \
        && $(trim_trailing "$(stderr_without_live_line "$result/stderr")") != "$(trim_trailing "$d_stderr")" ]]; then
        printf 'stderr mismatch (expected "%s")' "$d_stderr"
    elif [[ $d_has_stderr_contains -eq 1 ]] && ! grep -qaF -- "$d_stderr_contains" "$result/stderr"; then
        printf 'stderr mismatch (expected to contain "%s")' "$d_stderr_contains"
    fi
}

# Fills the stage-0 cache entry for one test file unless it is already there.
ensure_stage0() {
    local source=$1 entry=$2 scratch
    [[ -f $entry/done ]] && return
    scratch="$entry.tmp.$$"
    rm -rf "$scratch"
    mkdir -p "$scratch"
    compile_program 0 "$BEHAVIOUR_STAGE0" "$source" "$scratch/program" "$scratch"
    if [[ $(< "$scratch/compile.rc") -eq 0 ]]; then
        run_program "$scratch/program" "$scratch"
    fi
    [[ $BEHAVIOUR_KEEP_BINARIES -eq 1 ]] || rm -f "$scratch/program"
    : > "$scratch/done"
    if ! mv -T "$scratch" "$entry" 2>/dev/null; then
        rm -rf "$scratch"
    fi
}

# Checks one program and writes its row.
check_one() {
    local source=$1 name row display directory entry result bucket detail
    local mismatch compile_code run_code abnormal stage0_run stage0_live stage1_live
    if [[ $source == "$repo_root"/* ]]; then
        display=${source#"$repo_root"/}
    else
        display=$source
    fi
    name=${display%.ash}
    name=${name#tests/}
    row="$BEHAVIOUR_OUT/rows/${name//\//__}.tsv"
    emit() {
        printf '%s\t%s\t%s\n' "$name" "$1" "$(clean_detail "$2")" > "$row"
    }

    directory=$(dirname "$source")
    while [[ $directory == "$repo_root"/* ]]; do
        if [[ -f $directory/ashes.json ]]; then
            emit skipped "project test (compiled through ashes.json, not as a single file)"
            return
        fi
        directory=$(dirname "$directory")
    done

    parse_directives "$source"
    if [[ -n $d_skip ]]; then
        emit skipped "$d_skip"
        return
    fi

    entry="$BEHAVIOUR_CACHE/$BEHAVIOUR_STAGE0_KEY/$(sha256sum < "$source" | cut -c1-32)"
    ensure_stage0 "$display" "$entry"
    mismatch=$(expectation_mismatch "$entry")
    if [[ -n $mismatch ]]; then
        emit stage0-fails "$mismatch"
        return
    fi

    result="$BEHAVIOUR_OUT/stage1/${name//\//__}"
    rm -rf "$result"
    mkdir -p "$result"
    compile_program 1 "$BEHAVIOUR_STAGE1" "$display" "$result/program" "$result"
    compile_code=$(< "$result/compile.rc")
    abnormal=$(abnormal_exit "$compile_code" "$BEHAVIOUR_STAGE1_MEM" "$BEHAVIOUR_STAGE1_TIMEOUT")
    if [[ -n $abnormal ]]; then
        emit stage1-crash "compile $abnormal"
        return
    fi

    if [[ $d_compile_error -eq 1 ]]; then
        if [[ $compile_code -eq 0 ]]; then
            emit stage1-accepts-invalid "expected: $d_expected"
        elif ! grep -qaF -- "$d_expected" "$result/compile.log"; then
            emit output-differs "diagnostic lacks expected text: $(first_error_line "$result/compile.log")"
        else
            emit same ""
        fi
        [[ $BEHAVIOUR_KEEP_BINARIES -eq 1 ]] || rm -f "$result/program"
        return
    fi

    if [[ $compile_code -ne 0 ]]; then
        emit stage1-compile-fails "$(first_error_line "$result/compile.log")"
        return
    fi

    run_program "$result/program" "$result"
    [[ $BEHAVIOUR_KEEP_BINARIES -eq 1 ]] || rm -f "$result/program"
    run_code=$(< "$result/run.rc")
    stage0_run=$(< "$entry/run.rc")
    stage0_live=$(< "$entry/live")
    stage1_live=$(< "$result/live")
    abnormal=$(abnormal_exit "$run_code" "$BEHAVIOUR_RUN_MEM" "$BEHAVIOUR_RUN_TIMEOUT")
    if [[ -n $abnormal && $run_code -ne $stage0_run ]]; then
        emit stage1-crash "run $abnormal"
    elif ! cmp -s "$entry/stdout" "$result/stdout"; then
        emit output-differs "stdout (stage 0 \"$(head -c 60 "$entry/stdout" | tr -d '\0')\", stage 1 \"$(head -c 60 "$result/stdout" | tr -d '\0')\", stage 1 exit $run_code)"
    elif [[ $d_has_stderr -eq 1 || $d_has_stderr_contains -eq 1 ]] \
        && [[ $(stderr_without_live_line "$entry/stderr") != "$(stderr_without_live_line "$result/stderr")" ]]; then
        emit output-differs "stderr (the test declares an expected stderr)"
    elif [[ $run_code -ne $stage0_run ]]; then
        emit exit-differs "stage 1 exit $run_code (stage 0 exit $stage0_run)"
    elif [[ $stage0_live != "$stage1_live" ]]; then
        if [[ $stage0_live == none || $stage1_live == none ]]; then
            emit live-differs "stage 1 count missing or extra (stage0=$stage0_live stage1=$stage1_live)"
        elif [[ $stage1_live -gt $stage0_live ]]; then
            emit live-differs "stage 1 more (stage0=$stage0_live stage1=$stage1_live)"
        else
            emit live-differs "stage 1 fewer (stage0=$stage0_live stage1=$stage1_live)"
        fi
    else
        emit same ""
    fi
}

if [[ ${1:-} == --worker ]]; then
    check_one "$2"
    exit 0
fi

stage0=""
stage1=""
baseline=""
out=""
cache=""
jobs=${BEHAVIOUR_JOBS:-4}
files=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage0) stage0=${2:?--stage0 needs a path}; shift 2 ;;
        --stage1) stage1=${2:?--stage1 needs a path}; shift 2 ;;
        --baseline) baseline=${2:?--baseline needs a file}; shift 2 ;;
        --out) out=${2:?--out needs a directory}; shift 2 ;;
        --cache) cache=${2:?--cache needs a directory}; shift 2 ;;
        --jobs) jobs=${2:?--jobs needs a number}; shift 2 ;;
        -h | --help) usage; exit 0 ;;
        --) shift; files+=("$@"); break ;;
        -*) printf 'Unknown option %s\n' "$1" >&2; usage >&2; exit 2 ;;
        *) files+=("$1"); shift ;;
    esac
done
if [[ -z $stage0 || -z $stage1 ]]; then
    usage >&2
    exit 2
fi
for compiler in "$stage0" "$stage1"; do
    if [[ ! -x $compiler ]]; then
        printf 'Not an executable: %s\n' "$compiler" >&2
        exit 2
    fi
done
if [[ -n $baseline && ! -f $baseline ]]; then
    printf 'Baseline not found: %s\n' "$baseline" >&2
    exit 2
fi
[[ -n $baseline ]] && baseline=$(realpath "$baseline")

sources=()
if [[ ${#files[@]} -eq 0 ]]; then
    for file in "$repo_root"/tests/*.ash; do
        sources+=("$file")
    done
else
    for file in "${files[@]}"; do
        if [[ ! -f $file ]]; then
            printf 'No such test file: %s\n' "$file" >&2
            exit 2
        fi
        sources+=("$(realpath "$file")")
    done
fi

[[ -z $out ]] && out="$repo_root/work/behaviour/$(date +%Y%m%d-%H%M%S)"
[[ -z $cache ]] && cache="$repo_root/work/behaviour/stage0-cache"
mkdir -p "$out/rows" "$out/stage1" "$cache"
rm -f "$out"/rows/*.tsv

export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
export ASHES_RC_LEAKCHECK=1
export BEHAVIOUR_STAGE0=$(realpath "$stage0")
export BEHAVIOUR_STAGE1=$(realpath "$stage1")
export BEHAVIOUR_OUT=$(realpath "$out")
export BEHAVIOUR_CACHE=$(realpath "$cache")
export BEHAVIOUR_STAGE0_MEM=${BEHAVIOUR_STAGE0_MEM:-8G}
export BEHAVIOUR_STAGE0_TIMEOUT=${BEHAVIOUR_STAGE0_TIMEOUT:-120}
export BEHAVIOUR_STAGE1_MEM=${BEHAVIOUR_STAGE1_MEM:-4G}
export BEHAVIOUR_STAGE1_TIMEOUT=${BEHAVIOUR_STAGE1_TIMEOUT:-120}
export BEHAVIOUR_RUN_MEM=${BEHAVIOUR_RUN_MEM:-4G}
export BEHAVIOUR_RUN_TIMEOUT=${BEHAVIOUR_RUN_TIMEOUT:-30}
export BEHAVIOUR_KEEP_BINARIES=${BEHAVIOUR_KEEP_BINARIES:-0}

# The stage-0 executable is a small host that loads the compiler from the assemblies beside it,
# so the cache key covers those too, along with everything else a stage-0 result depends on.
BEHAVIOUR_STAGE0_KEY=$(
    {
        printf 'format %s\n' "$cache_format"
        printf 'caps %s %s %s %s\n' "$BEHAVIOUR_STAGE0_MEM" "$BEHAVIOUR_STAGE0_TIMEOUT" "$BEHAVIOUR_RUN_MEM" "$BEHAVIOUR_RUN_TIMEOUT"
        sha256sum < "$BEHAVIOUR_STAGE0"
        for assembly in "$(dirname "$BEHAVIOUR_STAGE0")"/*.dll; do
            [[ -f $assembly ]] && sha256sum < "$assembly"
        done
    } | sha256sum | cut -c1-32
)
export BEHAVIOUR_STAGE0_KEY

started=$(date +%s)
total=${#sources[@]}
printf 'Checking %s programs with %s jobs; results in %s\n' "$total" "$jobs" "$BEHAVIOUR_OUT" >&2

(
    while :; do
        sleep 30
        finished=$(find "$BEHAVIOUR_OUT/rows" -name '*.tsv' | wc -l)
        printf '  %s / %s after %ss\n' "$finished" "$total" "$(($(date +%s) - started))" >&2
    done
) &
progress=$!
trap 'kill "$progress" 2>/dev/null' EXIT

printf '%s\0' "${sources[@]}" | xargs -0 -n 1 -P "$jobs" bash "$script_path" --worker

kill "$progress" 2>/dev/null
wait "$progress" 2>/dev/null
trap - EXIT

results="$BEHAVIOUR_OUT/results.tsv"
cat "$BEHAVIOUR_OUT"/rows/*.tsv 2>/dev/null | LC_ALL=C sort > "$results"
recorded=$(wc -l < "$results")

summary="$BEHAVIOUR_OUT/summary.txt"
{
    printf 'stage 0: %s\nstage 1: %s\n' "$BEHAVIOUR_STAGE0" "$BEHAVIOUR_STAGE1"
    printf 'programs: %s, jobs: %s, wall time: %ss\n\n' "$recorded" "$jobs" "$(($(date +%s) - started))"
    awk -F '\t' '
        BEGIN {
            count = split("same stage1-compile-fails stage1-accepts-invalid output-differs exit-differs live-differs stage1-crash stage0-fails skipped", order, " ")
        }
        { buckets[$2]++ }
        END {
            printf "%-26s %6s\n", "bucket", "count"
            for (i = 1; i <= count; i++) {
                printf "%-26s %6d\n", order[i], buckets[order[i]]
                total += buckets[order[i]]
            }
            printf "%-26s %6d\n", "total", total
        }' "$results"
    printf '\nMost common details per bucket (grouped by the text before the first parenthesis):\n'
    for bucket in stage1-compile-fails stage1-accepts-invalid output-differs exit-differs live-differs stage1-crash stage0-fails skipped; do
        awk -F '\t' -v bucket="$bucket" '
            $2 == bucket {
                key = $3
                cut = index(key, "(")
                if (cut > 1) { key = substr(key, 1, cut - 1) }
                sub(/[[:space:]]+$/, "", key)
                if (length(key) > 100) { key = substr(key, 1, 100) "..." }
                groups[key]++
                if (!(key in example)) { example[key] = $1 }
            }
            END { for (key in groups) { printf "%d\t%s\t%s\n", groups[key], key, example[key] } }' "$results" \
            | LC_ALL=C sort -t "$tab" -k1,1nr -k2,2 | head -5 \
            | awk -F '\t' -v bucket="$bucket" '
                NR == 1 { printf "\n%s\n", bucket }
                { printf "  %5d  %s  (for example %s)\n", $1, $2, $3 }'
    done
} > "$summary"
cat "$summary"
printf '\nRows: %s\n' "$results"

if [[ $recorded -ne $total ]]; then
    printf 'Only %s of %s programs produced a row.\n' "$recorded" "$total" >&2
    exit 3
fi

if [[ -z $baseline ]]; then
    failing=$(awk -F '\t' '$2 != "same" && $2 != "stage0-fails" && $2 != "skipped"' "$results" | wc -l)
    if [[ $failing -gt 0 ]]; then
        printf '\n%s programs do not behave the same under stage 1.\n' "$failing"
        exit 1
    fi
    printf '\nEvery checked program behaves the same under stage 1.\n'
    exit 0
fi

comparison="$BEHAVIOUR_OUT/baseline-comparison.txt"
awk -F '\t' '
    FNR == NR { before[$1] = $2; before_detail[$1] = $3; next }
    {
        name = $1
        now = $2
        if (!(name in before)) {
            if (now == "stage1-crash") {
                regressions[++regression_count] = name ": not in the baseline, now " now " (" $3 ")"
            } else if (now != "same" && now != "skipped") {
                others[++other_count] = name ": not in the baseline, now " now " (" $3 ")"
            }
            next
        }
        was = before[name]
        if (was == now) {
            if (before_detail[name] != $3) {
                others[++other_count] = name ": still " now ", detail was (" before_detail[name] ") and is (" $3 ")"
            }
        } else if (now == "same") {
            improvements[++improvement_count] = name ": " was " -> same"
        } else if (was == "same" || now == "stage1-crash") {
            regressions[++regression_count] = name ": " was " -> " now " (" $3 ")"
        } else {
            others[++other_count] = name ": " was " -> " now " (" $3 ")"
        }
    }
    END {
        printf "Improvements (%d):\n", improvement_count
        for (i = 1; i <= improvement_count; i++) { print "  " improvements[i] }
        printf "\nRegressions (%d):\n", regression_count
        for (i = 1; i <= regression_count; i++) { print "  " regressions[i] }
        printf "\nOther changes, not failing (%d):\n", other_count
        for (i = 1; i <= other_count; i++) { print "  " others[i] }
        exit regression_count > 0 ? 1 : 0
    }' "$baseline" "$results" > "$comparison"
status=$?
printf '\nAgainst baseline %s\n\n' "$baseline"
cat "$comparison"
exit "$status"
