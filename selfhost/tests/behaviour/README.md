# Behavioural check: stage 1 against stage 0

`check.sh` compiles each test program with the stage-0 compiler and with the stage-1 compiler,
runs both binaries the way the test's directives say, and compares what they do: whether they
compile, their stdout, their exit code, and the number of heap cells still live at exit. It is the
acceptance check for stage-1 work that changes generated code without mirroring stage 0's IR.

It builds nothing. Both compilers must already exist.

## Running it

```bash
bash selfhost/tests/behaviour/check.sh --stage0 <path> --stage1 <path> \
    [--baseline <tsv>] [--out <dir>] [--cache <dir>] [--jobs N] [files...]
```

- With no files, every `tests/*.ash` is checked. Pass files (or a shell glob) to check a subset.
- `--out` defaults to `work/behaviour/<timestamp>`. It receives `results.tsv` (one
  `name<TAB>bucket<TAB>detail` row per program), `summary.txt`, `baseline-comparison.txt` when a
  baseline is given, and `stage1/<name>/` with stage 1's compile log, stdout and stderr.
- `--cache` defaults to `work/behaviour/stage0-cache`. Stage-0 results are stored there keyed by
  the stage-0 compiler (the executable and the assemblies beside it), the caps, and the test
  file's content, so a later run against a new stage-1 build only compiles with stage 1.
- `--jobs` defaults to 4.

The usual use is against the recorded state:

```bash
bash selfhost/tests/behaviour/check.sh --stage0 <path> --stage1 <path> \
    --baseline selfhost/tests/behaviour/baseline.tsv
```

Exit codes: without `--baseline`, non-zero when any program is in a bucket other than `same`,
`stage0-fails` or `skipped`. With `--baseline`, non-zero only when a program that was `same` is
not any more, or a program newly lands in `stage1-crash`; improvements, regressions and other
changes are printed as three lists. A changed detail inside the same bucket (a different live
count, a crash that dies differently) is listed under other changes and does not fail. To accept
a new state, copy the run's `results.tsv` over `baseline.tsv`.

Every compile and every program run goes through
`systemd-run --user --scope -p MemoryMax=<cap> -p MemorySwapMax=0 timeout <seconds>`. A cap kill
or a timeout is recorded as a result. Overridable by environment variable:

| Variable | Default | Applies to |
|---|---|---|
| `BEHAVIOUR_STAGE0_MEM` / `BEHAVIOUR_STAGE0_TIMEOUT` | `8G` / `120` | a stage-0 compile |
| `BEHAVIOUR_STAGE1_MEM` / `BEHAVIOUR_STAGE1_TIMEOUT` | `4G` / `120` | a stage-1 compile |
| `BEHAVIOUR_RUN_MEM` / `BEHAVIOUR_RUN_TIMEOUT` | `4G` / `30` | a compiled program |
| `BEHAVIOUR_JOBS` | `4` | parallel programs |
| `BEHAVIOUR_KEEP_BINARIES` | `0` | keep the compiled programs |

Both compilers run from the repository root (stage 1 finds `lib/Ashes` there), with
`ASHES_RC_LEAKCHECK=1` so the compiled program prints `ashes-rc-live N` as its last stderr line.
Stage 1 is compiled under `ulimit -s 1048576`.

## Buckets

Each program lands in exactly one, decided in this order.

| Bucket | Meaning |
|---|---|
| `skipped` | Not checked; the detail says why. |
| `stage0-fails` | Stage 0 does not meet the test's own expectation, so there is nothing to compare with. |
| `stage1-crash` | Stage 1's compile, or the program it produced, died by signal, timeout or cap kill where stage 0's did not. |
| `stage1-accepts-invalid` | The test expects a compile error and stage 1 compiles the program. |
| `stage1-compile-fails` | Stage 0 compiles the program and stage 1 rejects it. The detail is stage 1's first error line. |
| `output-differs` | Stdout differs byte for byte. Also a test expecting a compile error that stage 1 rejects without the expected text, and a stderr difference in a test that declares an expected stderr. |
| `exit-differs` | Same stdout, different exit code. |
| `live-differs` | Same stdout and exit code, different exit live-cell count. The detail carries both counts. |
| `same` | Stage 1 matches stage 0 in all of the above. |

## Directives

Read from the leading `//` block as the test runner does: `expect`, `expect-compile-error`,
`expect-stderr`, `expect-stderr-contains`, `exit`, `stdin`, `file`, `file-bytes`,
`executable-directory`, `working-directory` and `skip-on`. Each run gets a fresh fixture
directory holding the declared files and a copy of the binary; the directory's own path is
replaced by `<FIXTURE>` in captured output so the two runs compare equal.

Skipped:

- tests with `tcp-server` / `tcp-expect` / `tcp-send` or any `tls-*` directive, which need a
  loopback server and a port substituted into the source;
- tests with no `expect` directive, which the test runner skips too;
- tests under a directory with an `ashes.json`. The default set is the top level of `tests/`
  only, so the 110 programs in its subdirectories (projects, import fixtures, formatter and
  fuzz inputs) are not covered.

## Baseline

`baseline.tsv` was recorded on 2026-11-14 at `d1f2bdbe`, with the stage-0 Release build and the
stage-1 build `s1-f-bootstrap36-debug` (built from one commit earlier). 1,137 programs, 4 jobs,
306 s with an empty cache and 113 s with the stage-0 cache filled.

| Bucket | Programs |
|---|---|
| `same` | 796 |
| `stage1-compile-fails` | 125 |
| `live-differs` | 92 |
| `output-differs` | 73 |
| `stage1-accepts-invalid` | 11 |
| `stage1-crash` | 7 |
| `exit-differs` | 0 |
| `stage0-fails` | 1 |
| `skipped` | 32 |

Most common details per bucket:

- `stage1-compile-fails` (125), by the error's leading constructor:
  - 39 `UnknownLoweringBinding`: 14 `"async"`, 14 `"Ashes.Task.run"`, 3 other `Ashes.Task`
    names, 4 trait methods such as `"Ashes_Trait_Add.add"`;
  - 25 `ShippedPlanError`: 15 `ModuleImportCycle(["Ashes.Task.Parallel", "Ashes.Task.Parallel"])`,
    10 `UnknownImportExport` (6 `"Ashes.Text", "fromInt"`, 3 `"Ashes.IO", "print"`);
  - 24 `ShippedModuleMissing`: 14 `"Ashes.Task"` (12 of them from `"Ashes.IO"`), 9
    `"Ashes.Collection.Map.MapTree"`;
  - 15 `UnsupportedCoreLoweringExpression("non-core expression")`;
  - 9 `codegen: unsupported IrInstructionKind for this minimal slice`.
- `live-differs` (92): 50 with more live cells under stage 1, 41 with fewer, 1 where stage 1
  prints no count (`io_stderr_exit`). The commonest pairs are stage 0 2 or 3 against stage 1 1.
  The largest are `reuse_letbound_match_accumulator` (1 against 2,409,056),
  `tco_successor_children_shared_with_sibling_constructor_arm` (1 against 1,200,011),
  `recursive_map_bound_tail_plateau` (1 against 1,200,001) and `reuse_hashmap_set_bounded`
  (1 against 1,087,748).
- `output-differs` (73):
  - 53 are tests expecting a compile error that stage 1 does reject, but with its internal error
    value instead of the diagnostic text: 16 `UnknownLoweringBinding`, 6
    `CoreCallRequiresFunction`, 4 `ResourceUseAfterMove`, 3 each `ShippedParseError`,
    `UnsupportedCoreLoweringPattern`, `UnsupportedCoreBuiltinLowering`;
  - 20 are different stdout: 14 capability programs that stop with
    `Unhandled capability operation 'AshesPrivateType_capability_...'`, one that prints nothing
    (`capability_oneshot_lifo_posts`), and 5 that print wrong data
    (`closure_in_escaping_aggregate_keeps_captured_owner`,
    `closure_outliving_captured_let_owner_keeps_it`,
    `curried_stage_capture_outlives_caller_release`,
    `if_between_capturing_closures_releases_the_owner`,
    `returned_closure_applied_at_once_is_released`).
- `stage1-accepts-invalid` (11): 7 capability checks (`capability_closed_row_violation`,
  `capability_handle_incomplete`, `capability_provide_ambiguous`, `capability_provide_duplicate`,
  `capability_provide_missing_op`, `capability_unhandled`, `capability_unknown_in_uses`), plus
  `inline_annotation_unknown_type_reported`, `pattern_as_resource_alias_diagnostic`,
  `resource_consuming_helper_pipeline_stage_diagnostic` and `type_alias_cycle_diagnostic`.
- `stage1-crash` (7), all in the compiled program, none in the compiler:
  `choice_between_cells_onto_consumed_tail`, `mutual_recursion_tco_differing_arity`,
  `mutual_recursion_tco_heterogeneous_params`, `recursive_map_shared_tail`,
  `reused_list_cell_keeps_fresh_tuple_head`, `tmc_element_specialized_map_filter_stack`,
  `tuple_binder_hands_list_parameter_back`. How four of them die varies between runs (segfault,
  abort, 30 s timeout or the 4G cap).
- `stage0-fails` (1): `namespace_old_module_fails`. The stage-0 command line reports
  `Unknown standard library module 'Ashes.File'` where the test expects
  `reserved for the standard library`.
- `skipped` (32): 16 need a TCP loopback server, 9 a TLS one, 7 have no expectation.

## What a regression looks like

Run against the baseline with the older stage-1 build `s1-f-bootstrap33-debug` in place of
`s1-f-bootstrap36-debug`, the check exits 1 and lists 11 regressions, 5 improvements and 14 other
changes. The regressions are programs that are `same` in the baseline, for example:

```text
constrained_binding_late_pinned_argument: same -> stage1-compile-fails (MissingCoreTraitEvidence("Eq", SemVariable(37)))
keyed_call_result_accumulator_releases_its_keys: same -> live-differs (stage 1 more (stage0=1 stage1=201))
lambda_equality_typed_by_later_arguments: same -> output-differs (stdout (stage 0 "curried list eq, ...
```

Run against the baseline with the build it was recorded from, it exits 0 with no regressions.

## Attributing crashes to functions

Executables linked by the self-hosted compiler carry no symbol table, unwind tables or frame
pointers, so gdb shows `??` for every address. With `ASHES_LINK_MAP=<path>` set, the self-hosted
`compile` writes a link map beside the executable it links: `<path>` has one line per function,
`<hex address> <hex size> <name>`, sorted by address, and `<path>.names` names where each IR
function came from (`<label> <origin kind> <source function> <parent label>`, tab-separated). The
executable is the same with or without the variable. Name the map `<executable>.map` and the
scripts below find it on their own.

- `resolve-address.sh <map> <address>...` prints `symbol+offset` for each address.
- `link-map-symbols.sh <map> [<out>]` writes a symbol-only ELF file; `gdb -ex 'add-symbol-file
  <out>' --args <executable> ...` then names functions in backtraces and disassembly.
- `crash-trace.sh [--map <map>] -- <command...>` runs a command under gdb with core dumps off and
  reports the crash PC, gdb's frames in the executable's code, and every stack word that points
  into that code. The stack words are candidate return addresses, an approximate backtrace.
- `cluster-crashes.sh --compiler <path> --results <results.tsv>` reruns every `stage1-crash` row
  of a behavioural run under `crash-trace.sh --brief` (4 in parallel, each capped at 4G and 60 s)
  and prints one row per function the crashes happened in, most crashes first, with three
  example tests. Without a map it clusters by raw PC. Results go to `work/crash-clusters/`.

Set the stack limit stage-1 code needs before running any of them on a stage-1 or stage-2
compiler: `ulimit -s 1048576`.
