#!/bin/bash
# usage: suites.sh [name...]                 builds and runs the named self-hosted suites (the six by default)
#        suites.sh --focused [name...]       renders, builds and runs the focused fixtures (all by default)
#        suites.sh --all                     the six suites, then every focused fixture
#        suites.sh --list                    names the focused fixtures
#
# The six suites are compiled with the built stage-0 CLI. The focused fixtures are compiled with
# ASHES_SUITE_COMPILER (an `ashes` executable; default: the built stage-0 CLI through `dotnet run`), each compile
# under SUITE_COMPILE_CAP (default 24G) and each run under 4G, and print one line per run:
# `<fixture> <args> ok|FAILED ...`. A run passes when it exits 0 and, where the table names one, its stdout
# contains the expected text. Logs are in `$T/focused/`. Leak counters are not checked here; each fixture's
# README and validate.sh describe its plateau and poison checks.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
run_suite() {
    name=$1; shift
    rm -rf "selfhost/tests/$name/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "selfhost/tests/$name/ashes.json" > "$T/suite-$name.build" 2>&1
    bin=$(ls "selfhost/tests/$name/out/" 2>/dev/null | head -1)
    if [ -z "$bin" ]; then echo "$name COMPILE FAILED: $(grep -E 'ASH|rror' "$T/suite-$name.build" | head -2 | cut -c1-200)"; return; fi
    # The backend suite calls libLLVM at run time and finds it through its $ORIGIN RUNPATH: the bundled
    # copy and its dependencies go beside it, as stage 0 loads them, rather than the system library.
    if [ "$name" = "backend" ]; then cp runtimes/linux-x64/lib*.so* "selfhost/tests/$name/out/"; fi
    "selfhost/tests/$name/out/$bin" "$@" > "$T/suite-$name.out" 2>&1
    code=$?
    echo "$name exit=$code fails=$(grep -ci 'fail' "$T/suite-$name.out") last=$(tail -1 "$T/suite-$name.out" | cut -c1-90)"
}
run_six() {
    run_suite semantics
    run_suite frontend
    run_suite formatter
    run_suite projects
    run_suite cli
    run_suite backend lib/Ashes
}

# The focused fixtures, one per line: name | render command or - | project manifest, or file:<program.ash> |
# runs, separated by `;`, each the run's arguments (`-` for none) | expected stdout text, `file:<path>` for the whole
# stdout, or - for any.
# A rendered fixture's project lives under work/; the render command writes it.
focused_table() {
    cat <<'TABLE'
admissibility-cache-plateau|-|selfhost/tests/admissibility-cache-plateau/ashes.json|1000|cache ok
admissibility-worklist|bash scripts/selfhost-dev/render-admissibility-worklist.sh|work/admissibility-worklist/ashes.json|-|12288 graphs and special cases ok
argument-type-containment-batch|-|selfhost/tests/argument-type-containment-batch/ashes.json|check|24576 graph decisions and special cases passed
argument-type-containment-plateau|-|selfhost/tests/argument-type-containment-plateau/ashes.json|1000|done
backend-lookups|-|selfhost/tests/backend-lookups/ashes.json|-|index lookups passed
call-reach-index|-|selfhost/tests/call-reach-index/ashes.json|check|6912 call reach comparisons ok
consed-tail-call-refill|-|selfhost/tests/consed-tail-call-refill/ashes.json|-|consed tail call refill shapes ok
constrained-bindings-project-field|-|selfhost/tests/constrained-bindings/project-field/ashes.json|-|30
constrained-specialization|bash selfhost/tests/constrained-specialization/render.sh|work/constrained-specialization/check/ashes.json|1|concrete planning passed
constructor-field-reuse|bash selfhost/tests/constructor-field-reuse/render.sh|work/constructor-field-reuse/ashes.json|check|1000 field relations ok
constructor-group-index|-|selfhost/tests/constructor-group-index/ashes.json|check|1296 groups ok
constructor-lookup-index|bash scripts/selfhost-dev/render-constructor-lookup.sh|work/constructor-lookup-index/ashes.json|check|1296 lookup cases ok
core-copy-plan-view|bash scripts/selfhost-dev/render-core-copy-plan-view.sh|work/core-copy-plan-view/ashes.json|check|-
core-query-context|bash selfhost/tests/core-query-context/render.sh|work/core-query-context/ashes.json|check|core context checks passed
diagnostic-ir-dump|-|selfhost/tests/diagnostic-ir-dump/ashes.json|-|ok
dropper-admissibility-plateau|-|selfhost/tests/dropper-admissibility-plateau/ashes.json|cached 1000;miss 1000|done
dropper-index-check|-|selfhost/tests/dropper-index-check/ashes.json|1000|dropper index ok
dropper-view-check|-|selfhost/tests/dropper-view-check/ashes.json|cached 1000;miss 1000;synthesis 1000;copiers|-
dropper-worklist|bash selfhost/tests/dropper-worklist/render.sh|work/dropper-worklist/ashes.json|check|-
dropper-worklist-ir|bash selfhost/tests/dropper-worklist-ir/render.sh|work/dropper-worklist-ir-candidate/ashes.json|check|-
frontend-diagnostic-parity|-|selfhost/tests/frontend-diagnostic-parity/ashes.json|selfhost/parity/frontend/diagnostics|all self-hosted frontend diagnostic parity fixtures passed
frontend-token-parity|-|selfhost/tests/frontend-token-parity/ashes.json|selfhost/parity/frontend/tokens|all self-hosted frontend token parity fixtures passed
function-lookup-index|bash scripts/selfhost-dev/render-function-lookup.sh|work/function-lookup-index/ashes.json|1000|function indexes ok
generic-tuple-byte-parts|-|selfhost/tests/generic-tuple-byte-parts/ashes.json|1|tuple byte parts passed
generic-tuple-byte-parts-mirror|bash selfhost/tests/generic-tuple-byte-parts/render-mirror.sh|work/generic-tuple-byte-parts-mirror/ashes.json|1|tuple byte parts mirror passed
handoff-ownership-index|bash selfhost/tests/handoff-ownership-index/render.sh|work/handoff-ownership-index/ashes.json|check|-
heap-child-projections|bash scripts/selfhost-dev/render-heap-child-projections.sh|work/heap-child-projections/ashes.json|check|-
heap-classification-plateau|-|selfhost/tests/heap-classification-plateau/ashes.json|1000|done
heap-constructor-index|-|selfhost/tests/heap-constructor-index/ashes.json|check|125 indexed environments ok
heap-copy-plan-view|-|selfhost/tests/heap-copy-plan-view/ashes.json|check|-
heap-drop-worklist|bash scripts/selfhost-dev/render-heap-drop-worklist.sh|work/heap-drop-worklist/ashes.json|check|6144 graph roots and special cases agree
heap-shallow-copy-equivalence|-|selfhost/tests/heap-shallow-copy-equivalence/ashes.json|-|equivalent
inference-environment|-|selfhost/tests/inference-environment/ashes.json|-|captured environment inference: 25 cases passed
inference-fields|-|selfhost/tests/inference-fields/ashes.json|1|record field inference passed
ir-program-parity|-|selfhost/tests/ir-program-parity/ashes.json|selfhost/parity/semantics/lowered-ir|-
join-backedge-provenance|bash scripts/selfhost-dev/render-join-backedge-provenance.sh|work/join-backedge-provenance/ashes.json|1000|ok
lexer-unicode-classification|-|selfhost/tests/lexer-unicode-classification/ashes.json|@unicode-reference|all Unicode classifications match bundled PCRE2
logical-not-inference|bash selfhost/tests/logical-not-inference/render.sh|work/logical-not-inference/ashes.json|-|logical-not inference passed
logical-not-lifetime|bash selfhost/tests/logical-not-inference/render-lifetime.sh|work/logical-not-lifetime/ashes.json|1000|bindings passed
mixed-constrained-bootstrap|bash selfhost/tests/mixed-constrained-bootstrap/render.sh|file:work/mixed-constrained-bootstrap/Main.ash|1|mixed constrained lookup passed
mixed-constrained-snapshot-loop|bash selfhost/tests/mixed-constrained-bootstrap/render.sh|file:work/mixed-constrained-bootstrap/SnapshotLoop.ash|1|9
narrow-heap-consumers|bash selfhost/tests/narrow-heap-consumers/render.sh|work/narrow-consumers/ashes.json|check|equivalent
owned-call-result-worklist|-|selfhost/tests/owned-call-result-worklist/ashes.json|1000;borrowed1000;escape1000|-
owned-cons-argument-worklist|-|selfhost/tests/owned-cons-argument-worklist/ashes.json|1000|done
ownership-lookup|bash selfhost/tests/ownership-lookup/render.sh|work/ownership-lookup-regression/ashes.json|-|passed
parser-curried-result|-|selfhost/tests/parser-curried-result/ashes.json|-|parsed
persistent-dropper-context|bash selfhost/tests/persistent-dropper-context/render.sh|work/persistent-dropper-context/ashes.json|check|-
reach-ancestor-index|bash selfhost/tests/reach-ancestor-index/render.sh|work/reach-ancestor-index/ashes.json|check|equivalent
reach-constructor-index|bash selfhost/tests/reach-constructor-index/render.sh|work/reach-constructor-index/ashes.json|check 1 200|-
reach-count-index|bash selfhost/tests/reach-count-index/render.sh|work/reach-count-index/ashes.json|check|equivalent
recursive-replay|bash selfhost/tests/recursive-replay/render.sh|work/recursive-replay/ashes.json|-|file:work/recursive-replay/expected.txt
recursive-trait-evidence|-|selfhost/tests/recursive-trait-evidence/ashes.json|1|recursive trait evidence passed
returned-closure-index|bash scripts/selfhost-dev/render-returned-closure-index.sh|work/returned-closure-index/ashes.json|check|closure index ok
returned-constructor-field|-|selfhost/tests/returned-constructor-field/ashes.json|1000|done
root-layout-context|-|selfhost/tests/root-layout-context/ashes.json|check|-
semantics-diagnostic-parity|-|selfhost/tests/semantics-diagnostic-parity/ashes.json|selfhost/parity/semantics/diagnostics|all self-hosted semantic diagnostic parity fixtures passed
shared-graph-queries|-|selfhost/tests/shared-graph-queries/ashes.json|check;expected|-
stitching-lookup-index|-|selfhost/tests/stitching-lookup-index/ashes.json|check 1 200|-
type-graph-edges|bash selfhost/tests/type-graph-edges/render.sh|work/type-graph-edges/check/ashes.json|check|graph edge tests passed
type-graph-node-identity|bash selfhost/tests/type-graph-node-identity/render.sh|work/type-graph-node-identity/ashes.json|check|graph identity and collision tests passed
type-graph-path-cut|-|selfhost/tests/type-graph-path-cut/ashes.json|graphs;buckets|-
type-graph-path-cut-plateau|-|selfhost/tests/type-graph-path-cut-plateau/ashes.json|new1000;old1000|done
type-graph-search-plateau|-|selfhost/tests/type-graph-search-plateau/ashes.json|1000|done
type-resolution-index|-|selfhost/tests/type-resolution-index/ashes.json|-;1000|resolution index ok
unused-lambda-references|bash selfhost/tests/unused-lambda-references/render.sh|work/unused-lambda-references/ashes.json|-|unused lambda reference coverage passed
TABLE
}

compile_fixture() {
    local project=$1 output=$2 log=$3
    local cap=${SUITE_COMPILE_CAP:-24G}
    rm -f "$output"
    if [ -n "${ASHES_SUITE_COMPILER:-}" ]; then
        case "$project" in
            file:*) systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 timeout 3600 "$ASHES_SUITE_COMPILER" compile "${project#file:}" -o "$output" ;;
            *) systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 timeout 3600 "$ASHES_SUITE_COMPILER" compile --project "$project" -o "$output" ;;
        esac
    else
        case "$project" in
            file:*) systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 timeout 3600 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "${project#file:}" -o "$output" ;;
            *) systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 timeout 3600 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "$project" -o "$output" ;;
        esac
    fi > "$log" 2>&1
    [ -x "$output" ]
}

# Runs one compiled program with the given arguments under 4G; prints its verdict line.
run_fixture_program() {
    local name=$1 binary=$2 arguments=$3 expected=$4 stem=$5
    local -a argv=()
    if [ "$arguments" != "-" ]; then read -r -a argv <<< "$arguments"; fi
    if [ "${argv[0]:-}" = "@unicode-reference" ]; then
        argv=("$(bash scripts/selfhost-dev/generate-lexer-unicode.sh --verify 2>&1 | sed -n 's/^Reference byte per code point.*: //p')")
    fi
    (ulimit -s 1048576; ulimit -c 0; systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 1800 "$binary" "${argv[@]}") \
        > "$stem.out" 2> "$stem.err"
    local code=$?
    local matched=1
    case "$expected" in
        -) ;;
        file:*) cmp -s "${expected#file:}" "$stem.out" || matched=0 ;;
        *) grep -qF -- "$expected" "$stem.out" || matched=0 ;;
    esac
    if [ "$code" -eq 0 ] && [ "$matched" -eq 1 ]; then
        echo "$name [$arguments] ok: $(tail -1 "$stem.out" | cut -c1-90)"
    else
        echo "$name [$arguments] FAILED exit=$code: $(tail -1 "$stem.out" | cut -c1-90) $(tail -1 "$stem.err" | cut -c1-90)"
    fi
}

run_focused() {
    local name=$1 render=$2 project=$3 runs=$4 expected=$5
    local dir="$T/focused/$name"
    mkdir -p "$dir"
    if [ "$render" != "-" ] && ! bash -c "$render" > "$dir/render.log" 2>&1; then
        echo "$name RENDER FAILED: $(tail -1 "$dir/render.log" | cut -c1-160)"
        return
    fi
    if ! compile_fixture "$project" "$dir/program" "$dir/compile.log"; then
        echo "$name COMPILE FAILED: $(grep -E 'ASH[0-9]+|rror' "$dir/compile.log" | head -2 | cut -c1-200)"
        return
    fi
    local IFS_SAVE=$IFS run index=0
    IFS=';'
    for run in $runs; do
        IFS=$IFS_SAVE
        index=$((index + 1))
        run_fixture_program "$name" "$dir/program" "$run" "$expected" "$dir/run$index"
        IFS=';'
    done
    IFS=$IFS_SAVE
}

# Each row of a cases table compiles one program file and requires the given text on its stdout, or, for a row
# whose expectation starts with `compile must fail`, a failed compile.
run_cases() {
    local group=$1 table=$2 base=$3
    local dir="$T/focused/$group" name file expected
    mkdir -p "$dir"
    while IFS=$'\t' read -r name file expected; do
        [ -z "$name" ] && continue
        [ -n "$base" ] && file="$base/$name"
        case "$expected" in
            "compile must fail"*)
                if compile_fixture "file:$file" "$dir/$name" "$dir/$name.compile.log"; then
                    echo "$group/$name FAILED: compiled, expected a compile error"
                else
                    echo "$group/$name ok: rejected ($(grep -E 'ASH[0-9]+|rror' "$dir/$name.compile.log" | head -1 | cut -c1-120))"
                fi ;;
            *)
                if compile_fixture "file:$file" "$dir/$name" "$dir/$name.compile.log"; then
                    run_fixture_program "$group/$name" "$dir/$name" "-" "$expected" "$dir/$name"
                else
                    echo "$group/$name COMPILE FAILED: $(grep -E 'ASH[0-9]+|rror' "$dir/$name.compile.log" | head -2 | cut -c1-200)"
                fi ;;
        esac
    done < "$table"
}

run_focused_named() {
    local wanted=$1 found=0
    case "$wanted" in
        constrained-bindings)
            run_cases constrained-bindings selfhost/tests/constrained-bindings/cases.tsv ""
            return ;;
        inference-field-queries)
            # query-cases.tsv rows are `file<TAB>expected`; the name column doubles as the file name.
            awk -F'\t' '{ print $1 "\t-\t" $2 }' selfhost/tests/inference-fields/query-cases.tsv > "$T/inference-field-queries.tsv"
            run_cases inference-field-queries "$T/inference-field-queries.tsv" selfhost/tests/inference-fields/queries
            return ;;
    esac
    while IFS='|' read -r name render project runs expected; do
        if [ "$name" = "$wanted" ]; then
            found=1
            run_focused "$name" "$render" "$project" "$runs" "$expected"
        fi
    done < <(focused_table)
    [ "$found" -eq 1 ] || echo "$wanted: no such focused fixture (suites.sh --list)"
}

run_all_focused() {
    local name
    for name in $(focused_table | cut -d'|' -f1) constrained-bindings inference-field-queries; do
        run_focused_named "$name"
    done
}

case "${1:-}" in
    --list)
        focused_table | cut -d'|' -f1
        echo constrained-bindings
        echo inference-field-queries ;;
    --focused)
        shift
        if [ $# -eq 0 ]; then run_all_focused; else for s in "$@"; do run_focused_named "$s"; done; fi ;;
    --all)
        run_six
        run_all_focused ;;
    "")
        run_six ;;
    *)
        for s in "$@"; do
            if [ "$s" = "backend" ]; then run_suite backend lib/Ashes; else run_suite "$s"; fi
        done ;;
esac
