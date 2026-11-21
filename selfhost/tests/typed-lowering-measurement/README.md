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

Progress markers in stderr bracket project loading and stitching, inference, and the lowering measurement. The lowering API runs the first pass and optional replay in one call. Its completion marker reports whether replay ran. All progress writes occur outside the reported timing intervals.

A successful run exits zero and prints a TSV header and one data row. A project, parse, inference or lowering error terminates the run with a diagnostic. A lowering failure can still produce a partial counter row, so check the exit status before using the data. Keep the stage-0 and driver source revisions with the output.

| Columns | Meaning |
| --- | --- |
| `mode`, `stitch_ms`, `infer_ms` | Selected lowering mode, time to load/parse/stitch, and time to identify and infer. |
| `first_ms`, `replay_ms`, `replayed` | Measured first traversal and replay durations and whether replay ran. The API exposes a Boolean. Replay time includes any retained-program traversal required before emission. |
| `pending_traits`, `equality_goals` | Trait goals and structural equality goals left by the first traversal. |
| `first_*`, `replay_*` | For each observed pass, operators answered from recorded types, deferred operators, function bodies lowered again, calls using recorded expected results, and calls retried after that recorded expectation failed. Absent replay has zero counters. |
| `pending_keys` | Space-separated pending trait site keys. Empty when there are no keys. |

The typed counter gate requires zero pending traits, zero equality goals and `replayed=no`. Record the body re-lowering count separately. Measurements use the Core lowering API with reuse enabled and a manifest-level source context. The production CLI, backend, stage-2/3 comparison and behaviour suites provide separate acceptance evidence.
