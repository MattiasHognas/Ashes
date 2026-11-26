# Self-Hosting: Building the Ashes Compiler in Ashes

This is the one document for replacing the C# compiler (stage 0) with the self-hosted compiler
(stage 1): what is being done now, where things stand, and what comes after. Git history holds
everything earlier.

## Goal

Done means:

- stage 1 builds stage 2, stage 2 builds stage 3, and stage 2 and stage 3 are byte-identical;
- stage 1's behaviour matches stage 0's, diagnostics and execution included;
- typed lowering is the only lowering (step 8's remaining entries and step 9 below);
- code generation and linking run in parallel;
- no leaks remain, in either compiler.

A successful stage-1 build or a focused test is never a substitute for these gates.

## Tasks

This is the only task list. Each task ends with a pull request that is merged before the next
starts.

### Current

| # | Task | Done when | State |
| --- | --- | --- | --- |
| 1 | Finish round 8 on `selfhost/round11`, fix what breaks, merge PR #1161 | PR #1161 is merged | Done: merged as `3432916f` |
| 2 | Bring every leak fix written so far onto main (no new leak fixes), measure the build with them, fix only what breaks | The fixes are on main and a round with them passes every check in "How a round is run" | Done: merged from `selfhost/round12` after round 12. Left out: the pass-through loop-parameter placement (it leaked 18,000 cells on one test). Merged but not yet effective in stage 1: the matched-tail rule and the self-call hand-over |
| 3 | Give `Ashes.Task.Parallel` workers their own libc thread-local storage (one worker); replace step 8's deferred entries 4 and 6–11 (another worker) | A worker that calls into libc no longer corrupts the heap; every entry in the step 8 table is Done | In progress: the parallel runtime is done (round 13); step 8 entry 8 and the unknown-closure general-rc results are done, entry 9 closed with no code; entries 6, 7, 10, 11 and 4 remain |
| 4 | Step 9: delete the raw lowering and retire the 258 lowered-IR parity fixtures | `CoreLowering.Raw` and everything only it reaches are gone, with the fixtures and their harnesses | Not started |
| 5 | All workers on leaks, highest impact first, measured and merged per batch; the first batch is the known wrong-output bugs | The behaviour check reports no program differing in live cells, and a compiler built by either stage leaves no live cell after compiling the compiler | Not started |

### Next

| # | Task | Done when | State |
| --- | --- | --- | --- |
| 6 | Close the remaining stage-1/stage-0 differences: the diagnostics whose text or position differs, and the 32 of 95 whole-program IR parity fixtures if step 9 has not retired them | The behaviour check, plain and under `ASHES_RC_POISON=1`, reports every program as same, and no compile-error test prints a different diagnostic | Not started |
| 7 | CLI parity: port the commands `test`, `repl`, `login`, `publish`, `yank`, `search` and `info`, the options `--target`, `--target-cpu`, `-O0`–`-O3`, `--expr`, `--emit-ir`, `--parallel-workers` and `--parallel-stack-size`, and `restore` beyond path dependencies (`Dispatch.ash` and `Compile.ash` in `selfhost/packages/cli` against [the CLI reference](../reference/cli.md)) | Every command and flag in the CLI reference works from the self-hosted binary, or the reference says it is not provided | Not started |
| 8 | Port linux-arm64, win-x64 and win-arm64 code generation and linking (`Compile.ash` hard-codes `x86_64-unknown-linux-gnu`; the linker writes ELF only) | Binaries built by the self-hosted compiler pass the cross-target validation the C# compiler passes: qemu for linux-arm64, wine for win-x64, structural checks for win-arm64 | Not started |
| 9 | Debug information: emit DWARF and accept `--debug`/`-g` (the self-hosted backend has no debug-info builder) | The [debugging guide](../guide/debugging.md)'s workflow works on a self-hosted build | Not started |
| 10 | Tooling: decide the test runner, the language server and the debug adapter, which exist only in C# (`src/Ashes.TestRunner`, `src/Ashes.Lsp`, `src/Ashes.Dap`; the C# CLI and LSP also use `src/Ashes.Formatter`) | Each runs on the self-hosted compiler's phases or is recorded here as staying in C# with the reason | Formatter: self-hosted (`selfhost/packages/formatter`, wired to `fmt`); the other three not started |
| 11 | Performance and memory: stage 3 needs 37–48 GiB where stage 2 needs 13–17 GiB; the compiler's 2,657 body re-lowerings if step 3 has not removed them; and the borrowed-result copy: a call whose result is a borrowed part of its argument (a `Str` or `Bytes` field, a pattern binding, a list head, a tuple component) copies the part in both compilers, where only the trivial field accessor retains it | Stage 3 builds within stage 2's memory, and a time and memory gate is recorded in this document | Not started |
| 12 | Decide what layout an unconstrained element type gets (a loop parameter whose element type stays a free type variable is never released, in both compilers) | The language reference states the rule and both compilers implement it | Not started |
| 13 | Make the self-hosted compiler the default toolchain and retire the C# compiler | Releases for all four targets are built by the self-hosted compiler from a pinned, reproducible stage-0 input; CI builds, bootstraps and checks it (no job in `ci/` or `.github/` mentions `selfhost` today); the `src/` compiler projects are removed | Not started |

## Where the compiler stands

Measured at round 13: main with the parallel runtime fix, step 8 entry 8 and the unknown-closure change.

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | 27 min, 20 GiB |
| Stage-1 behaviour against stage 0 (1,291 programs) | 1,170 same, 121 differ only in live cells at exit, 0 other |
| The same under `ASHES_RC_POISON=1` | 1,163 same, 122 live cells only, 6 wrong output or crash (below) |
| Stage 2 built by stage 1, defaults (16 partitions, 6 processes) | 601 s, 18.5 GiB |
| Stage 2 as a single module (`ASHES_LLVM_JOBS=1`) | 875 s, 14.7 GiB |
| Stage 3 built by stage 2 | 615 s, byte-identical to stage 2 |
| Stage 3 memory, resident plus swap | 32.4 GiB (40.4 GiB before the unknown-closure change: seven call sites in the compiler stopped asking their callback for an arena result) |
| Live cells when a stage-1-built compiler finishes compiling the compiler | 368.0 million, 14.6 GiB resident (round 12; not remeasured) |
| The same on the frontend test project: stage-1-built against stage-0-built | 20.6 million cells, 2.28 GB against 5.5 million, 1.21 GB |
| Semantics suite | 98 of 99 groups (whole-program IR parity 32 of 95 differing) |
| Backend suite | 497 of 499 (one live-cell count case, twice) |
| C# tests | 3,092 of 3,093 (one memory-plateau test, below) |

- Typed lowering is the only lowering the shipped compiler reaches, with no switch and no fallback.
  The raw lowering survives as a test-only path in `CoreLowering.Raw` until step 9.
- Partitioned multi-process code generation is the default above 2,048 lifted functions.
  `ASHES_LLVM_JOBS=1` selects one module; `ASHES_LLVM_PROCESSES=1` builds partitions in process.

## Known open defects

Wrong output, found by the behaviour check under `ASHES_RC_POISON=1` in round 12.

| Defect | Stage | Fix |
| --- | --- | --- |
| A view of a let-bound call result escaping in a record, tuple or closure read freed memory | 0 and 1 | Fixed for `uncons`, `unconsText` and `subView`; `fromText` still fails on stage 1 (`byte_views_of_let_bound_call_results_in_every_position`) |
| `arena_record_result_owned_field_moves_into_copy`, `arena_record_result_read_before_return_moves_field` print 45 for 55 | 1 | Not written |
| `rc_escaping_view_backing_released` prints garbage | 1 | Not written |
| `mixed_tuple_join_taken_apart_into_constructor` segfaults | 1 | Not written |
| A deep copy in a generic helper lowered before its type settles hands the caller's string over uncopied | 0 | Not written (stage 1 is right since step 8 entry 5) |
| Regular expressions used by several `Ashes.Task.Parallel` workers at once crash or answer wrongly: the PCRE2 payload's `malloc` is a bump allocator over one region whose cursor is a plain global every thread shares | runtime, both | Not written; reproduction: `Ashes.Text.Regex.compile` and `isMatch` in a loop inside `Ashes.Task.Parallel.map` over eight elements |

Leaks, as known:

- One C# memory-plateau test still fails (`loop_rebuilding_list_of_lists_onto_matched_inner_lists`: a constructor-spine rebuild over a tuple scrutinee leaks the list of lists in stage 0).
- Test corpus: 25 programs leave more live cells under stage 1 than under stage 0, about 97,000
  cells in total, nearly all in three constrained-loop tests that wait on the matched-tail rule;
  97 programs leave fewer.
- The leak fixes so far cleared the test corpus but not the compiler's own footprint: stage 3's peak
  moved from 42.2 to 40.3 GiB. Step 5 starts from a breakdown of the 368.0 million cells by type
  and allocation site.

## Type inference before lowering

The inference (`ProgramInference.ash`, `TypeInference.ash`) is stage 1's type checker. It runs on
the stitched program before lowering, rejects with stage 0's codes and messages, and fills a node
table: `NodeIdentity.ash` numbers every expression node, and `ProgramInferenceResult.nodeTypes`
records a type per expression, the trait and operand type per operator, the instantiation and
dictionaries per call, and the scheme per binding. Lowering reads its types from that table. The
measurement driver `selfhost/tests/typed-lowering-measurement` (`sweep.sh` for the corpus; see its
README) counts replays, goals, deferred operators and re-lowerings, and per entry below a shadow
count of the sites where the current decision and the final-type decision agree, differ, or have no
record.

### Step 8: rules over the final type

Each entry is a lowering decision keyed on whether a type is still open at a site, replaced by a
rule over the recorded final type. A replacement with differing sites gets, per changed program, a
per-function diff of `--emit-ir lowered`, the behaviour check with `ASHES_RC_LEAKCHECK=1`, and a
lifetime regression in `tests/`. Shadow counts are agree / differ / unrecorded on main after the
leak merge (driver at `3629bf19`, which separates arena-request sites closed by the body's
instantiation from sites closed only by the variables the program's uses settle). Every unrecorded
site carries its reason; for entry 8 they are recorded types still open (the request is kept), no
node, or settled only (entry 8b).

| Entry | Rules | Replacement | State | Corpus, 1,184 programs | Whole compiler |
| --- | --- | --- | --- | --- | --- |
| 1 | `emitDeferredCoreAdd`, `emitDeferredCoreEquality` | an open operand is an error, never the integer form | Done | | |
| 2 | `unprovenTraitGoalKey`, `openDictionaryGoalKey`, `pinProvenTraitGoal`, `pinProvenEqualityGoal` | an open goal is refused at its site | Done | | |
| 3 | `activeEvidenceCoversOperator`, `traitPlanLacksActiveEvidence` | dispatch from the recorded operator instance | Done | | |
| 5 | `lowerInternalDeepCopy` | the copy at the value's recorded type | Done | | |
| 8 | `requestsArenaResult`, `containsUnresolvedLayout` | the call's result pinned to its recorded type closed by the body's own instantiation before the request (`b898495b`), except through an unknown closure (`56564990`) | In review | 25,747 / 4 / 5,643, 4 programs, all four through an unknown closure, so they keep the request | 43,934 / 15 / 265; after: 0 differ |
| 8c | an unknown closure's result with a fixed copy-out (a string, a list of strings or scalars) | take it by the returns bit and normalize it after the call as stage 0 does (`CopyOutList` with the release), instead of the arena request | Later item (leak focus) | | 6 `List`, 1 `Unit` sites keep the request |
| 8b | the same rule for a result closed only by the variables the program's uses settle | a generic body lowered once (`Collection.Map`, `Collection.List`), which stage 0 lowers with its variables open | Deferred, after entry 8's round | 560 sites would drop the request | 128 sites would drop the request |
| 4 | `bodyEncounteredRequirementClosedScope` | recorded scope types from the start; reads parameters at entry, so it waits for 11 | Deferred | 0 / 8,839 / 0, 832 programs | 0 / 2,314 / 0 |
| 6 | `selfResultDeferred` | the binding's recorded result layout decides the arena request | Deferred | 14 / 215 / 2,786, 117 programs (258 of the unrecorded closed only by settled variables) | 250 / 975 / 97 |
| 7 | `siblingResultOpen`, `siblingResultPinned`, `deferredSelfResultNamedByConsumer`, `keepFirstSelfResultDeferral` | the group member's recorded result layout decides; the largest IR change | Deferred | 0 / 69 / 5, 36 programs | 0 / 1,851 / 51 |
| 9 | `calleeResultUnsettled` | none under callee-first order: every differing site is a cycle (see below) | Deferred | 37 / 237 / 0, 234 programs | 113 / 22 / 0 |
| 10 | `lowerFunctionBodyResolvingCalls`, `lowerRecursiveBodyResolvingCalls`, `anyCallResultResolved` | none: with 6 to 9 every body is lowered once | Deferred | 0 / 502 / 0, 248 programs | 0 / 353 / 0; after entry 8: 0 / 348 / 0 |
| 11 | `normalizesAlwaysReturnedParameter` and the parameter-to-result arena boundary | taken when the recorded type needs it, with the release on every exit first (eleven programs leaked without it) | Deferred | 32,838 / 8 / 9,005, 7 programs | 27,021 / 8 / 2,881 |

Entry 8 first pinned every open call result, including calls through a closure no name resolves
(`given (f) -> f(x)`). The round showed why stage 0 asks the arena there: the caller cannot own
what an unknown callee returns, and stage 0 copies the result out after the call. Taking a list
result by the callee's returns bit left the list and its strings unreleased (7 and 13 live cells
against stage 0's 1). The pin now leaves those calls alone (`56564990`), which covers all four corpus
sites and leaves their IR as before; on the compiler body re-lowerings fall from 2,667 to 2,662
and entry 10's sites from 353 to 348. An unknown closure's result becomes ownable only when the
caller reads the callee's returns bit for every result shape. A general reference-counted result
(a record or ADT outside the fixed copy-outs) already is, as in stage 0, which asks no arena for it:
`05b82e35` takes it owned through the returns bit where its type is closed by the instantiation (7
of the 11 compiler sites, four of them `LoweredCoreValue` lowering callbacks; three corpus programs). The copy-out kinds
are entry 8c.

Entry 9, decided on 2026-11-26: callee-first lowering in dependency order, recursive groups and
closures staying unsettled as stage 0's `CalleeResultUnsettled` leaves them. Measured with the
reason each site now names (driver at `350b7717`): of the 237 corpus sites and the 22 compiler
sites, 235 and 22 are a function's call to itself and 2 (`Text.Json`'s `parseValue` called from
the `parseArr` nested in its own body) are a call into the body still being lowered. None is a
forward reference to another top-level body: top-level scoping already lowers every callee
before its callers (a binding is visible only to later declarations, and imported modules are
stitched before their importers). So callee-first order changes nothing, the entry closes with
no code under that decision, and lowering order, lambda and label numbering stay as they are.
The fact a self call lacks is its own body's result placement, which only predicting it before
lowering (the other option, beyond stage 0) could supply.

Planned order: 8 and 9, then 6 and 7 with 10, then 11, then 4. Replacing the per-binding plan
(`constrainedBindingPlan`) by the recorded schemes follows entry 3. Entries 6 to 9 mirror rules
stage 0 has on its live types (`RequestsArenaResult`, `ContainsUnresolvedLayoutType`,
`CalleeResultUnsettled`, `EmitRuntimeManagedTcoArgumentNormalization`), so they change generated
code by design.

### Step 9: delete the raw lowering

- Delete `CoreLowering.Raw` entirely, the raw parts of `CoreLowering.Measurement` and
  `CoreLowering.RecordedTypes`, the replay and no-table branch of `CoreLowering.Program`, and,
  once step 8 is done, discovery, pending goals, deferred operators, the per-binding plan and body
  re-lowering.
- Blockers: the 258 lowered-IR parity fixtures (`selfhost/parity/semantics/lowered-ir`, driven by
  `LoweredIrFixtures`, `ir-program-parity`, `semantics-diagnostic-parity` and the backend raw
  fixtures), retired rather than regenerated; and the selfhost semantics tests that import
  `CoreLowering.Raw` (about thirty files), deleted or moved to the numbered entry points.

## How a round is run

The coordinator runs every build, suite and measurement and pushes every branch. Workers implement
in their own worktrees under `.claude/worktrees/`, run quick checks only, commit without attribution
trailers, and do not push. A round on the integration worktree (`step-f-round3`):

1. Integrate commits and run the whole-compiler inference check.
2. Build stage 1 from stage 0.
3. Run the REST checks.
4. Run the behaviour check (`selfhost/tests/behaviour/check.sh`), plainly and under
   `ASHES_RC_POISON=1`.
5. Build stage 2, then stage 3, and compare them byte for byte.
6. Run the stage-2 behaviour check.
7. Run the semantics and backend suites (`scripts/selfhost-dev/suites.sh`).
8. For C# changes, the C# build, tests and format check; before a pull request merges,
   `scripts/verify.sh`.

Limits and rules:

- Stage 3 runs under `MemoryMax=40G MemorySwapMax=32G`; sample `VmRSS` and `VmSwap` together.
- At most two compiler builds at once on this 60 GiB machine; anything above 8 GiB starts only
  when 35 GiB is available and at most one other compiler build is running.
- Every compiled program runs under a 4 GiB cap; single compiles under 8 GiB.
- A running queue script does not see edits to its file: stop it by PID and relaunch. Never kill
  by name pattern. Leave a waiter on every build queue.
- Check a probe's `source.commit` before attributing its rows to a commit.
- Never modify a worktree a job is reading.
- Fix the compiler, never the witness. Pair every stage-0 fix with its stage-1 port in the same
  pull request.
- No development tags; never rewrite `main`'s history or touch `release/*`.
- Stage 1 has no `test` command; run a project test through `compile --project`.
- A stage-1-built binary that crashes: `selfhost/tests/behaviour/crash-trace.sh --map` with its
  link map (`ASHES_LINK_MAP=<path>`). Under gdb, start the program as gdb's child; attaching is
  not permitted.
- A leak: take a census of what is live at exit for a tiny input, add one construct and diff the
  census, then put a hardware watchpoint on one leaked cell's count to find the unmatched retain.

Switches in use: `ASHES_RC_LEAKCHECK` and `ASHES_RC_POISON` (read at compile time),
`ASHES_LLVM_JOBS`, `ASHES_LLVM_PROCESSES`, `ASHES_DIAGNOSTIC_LLVM_STATS=<path>`,
`ASHES_DIAGNOSTIC_IR_PREFIX=<prefix>`, `ASHES_LINK_MAP=<path>`.

## Reference

[Language reference](../reference/language.md) · [Architecture](../internals/architecture.md) ·
[Testing](../guide/testing.md) ·
[Self-hosted toolchain README](https://github.com/MattiasHognas/Ashes/blob/main/selfhost/README.md)
