# Typed lowering measurement

Run from the repository root. The driver reads shipped modules from `lib/Ashes` and takes a project manifest and either `legacy` or `typed`. Both modes load and stitch the project, number its syntax and run inference. Legacy lowering receives the original stitched program and closed top-level declaration schemes for arithmetic specialization. Typed lowering receives the identified program and its recorded node types. Run each mode in a separate process with the same driver binary and input.

Build with a current stage-0 compiler in the coordinator’s shared build slot. Set `S0` to its absolute path and `OUT` to a fresh absolute directory. The example measures the self-hosted CLI.

```bash
set -e
ulimit -s 1048576
ulimit -c 0
mkdir -p "$OUT"
systemd-run --user --scope -q -p MemoryMax=24G -p MemorySwapMax=0 timeout 5400 \
    "$S0" compile --project selfhost/tests/typed-lowering-measurement/ashes.json \
    --debug -O0 -o "$OUT/measure" >"$OUT/build.log" 2>&1
systemd-run --user --scope -q -p MemoryMax=12G -p MemorySwapMax=0 timeout 3600 \
    "$OUT/measure" selfhost/packages/cli/ashes.json legacy >"$OUT/legacy.tsv" 2>"$OUT/legacy.err"
systemd-run --user --scope -q -p MemoryMax=12G -p MemorySwapMax=0 timeout 3600 \
    "$OUT/measure" selfhost/packages/cli/ashes.json typed >"$OUT/typed.tsv" 2>"$OUT/typed.err"
```

Progress markers on stderr bracket project loading and stitching, inference, and lowering. If discovery requests replay, the driver reports the transition before replay begins. Progress writes are excluded from the reported timing intervals. The final stderr summary gives the number of traversals (one discovery, optionally one retained discovery and one replay emission) and the body re-lowering counters for each. As in the TSV, each counter describes the lowering state kept by its traversal.

Add `--goal-details` after the mode to capture lexical evidence when a site is deferred. The report on stderr includes the stitched source region, the start and end offsets in the goal key, trait and method, numbered node ID where available, original and settled operand types, and the active trait-method bindings at that occurrence. Structural equality keys also appear on stderr without this option. Detailed collection adds work inside lowering, so use the same option for both modes when comparing times.
The details also name, as `deferred-operator goal` lines, every `+`, `-`, `*`, `==` and `!=` the first pass emitted in its speculative integer form because both operands were still open, whether or not a replay follows; a site with no span is keyed `unspanned:<trait>:<method>`. Their number equals `first_deferred_operators` minus the deferred ordered comparisons, which appear as trait goals. A body lowered again can name a site once per lowering. In `typed` mode each detail line ends with what the table recorded for the site's node: `recorded_operator=<trait>(<operand type>) <primitive|declared|dictionary>` and `recorded_type=`. `recorded_operator=none` means the site was lowered at a node lowering synthesized, under the identity of the node around it; an operand type that is a bare variable means a generic body lowered without an instantiation that closes it.
The traversal report also prints `concrete_copy_calls` counts for the first and emission passes on stderr. A recorded call passed the source-copy proof using its node facts. An inferred call entered the existing speculative inference path. The counts describe the state retained by each pass, including cache hits, and exclude discarded attempts and the optional retained-only traversal.

A successful run exits zero and prints a TSV header and one data row. A project, parse, inference or lowering error terminates the run with a diagnostic. A lowering failure can still produce a partial counter row, so check the exit status before using the data. Keep the stage-0 and driver source revisions with the output.

| Columns | Meaning |
| --- | --- |
| `mode`, `stitch_ms`, `infer_ms` | Selected lowering mode, time to load/parse/stitch, and time to identify and infer. |
| `first_ms`, `replay_ms`, `replayed` | Measured first traversal and replay durations and whether replay ran. The TSV keeps its Boolean replay indicator. Replay time includes any retained-program traversal required before emission. |
| `pending_traits`, `equality_goals` | Trait goals and structural equality goals left by the first traversal. |
| `first_*`, `replay_*` | For each observed pass, operators answered from recorded types, deferred operators, function bodies lowered again, calls using recorded expected results, and calls retried after that recorded expectation failed. Absent replay has zero counters. |
| `pending_keys` | Space-separated pending trait site keys. Empty when there are no keys. |

The typed counter gate requires zero pending traits, zero equality goals and `replayed=no`. Record the body re-lowering count separately. Measurements use the Core lowering API with reuse enabled and a manifest-level source context. The production CLI, backend, stage-2/3 comparison and behaviour suites provide separate acceptance evidence.

## Test-program sweep

The driver also takes a single `.ash` file in place of a manifest; it is stitched with the shipped modules it imports, as `compile <file>` does. `sweep.sh <measure> <typed|legacy> <out>` runs it over every `tests/*.ash` that is not an `expect-compile-error` fixture, one program at a time under a 4 GiB cap, and writes `rows.tsv` (the program name, then the 19 columns above), `failed.txt` (programs whose run did not exit zero with a complete row: stitch or lowering errors), `rejected.txt` (programs the inference rejects, such as the `_fails` diagnostics fixtures; `__TCP_PORT__` is replaced by a number first, as the test runner substitutes a port) and `summary.txt` (programs, replays, pending trait goals, structural equality goals, first-pass deferred operators). The goal details of every program that replayed or deferred an operator are kept in `<out>/<name>.err`. Run it from the repository root with the same driver binary for both modes.
