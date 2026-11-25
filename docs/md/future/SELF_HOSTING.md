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
| 2 | Bring every leak fix written so far onto main (no new leak fixes), measure the build with them, fix only what breaks | The fixes are on main and a round with them passes every check in "How a round is run" | In progress on `selfhost/round12`: the leak and use-after-free fixes are merged; round 9 validates them with a rebuilt stage 0. Left out: the pass-through loop-parameter placement (it leaked 18,000 cells on one test). The stage-1 half of the matched-tail rule does not fire yet |
| 3 | Give `Ashes.Task.Parallel` workers their own libc thread-local storage (one worker); replace step 8's deferred entries 4 and 6–11 (another worker) | A worker that calls into libc no longer corrupts the heap; every entry in the step 8 table is Done | Not started |
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

Measured at round 8, the state PR #1161 merged.

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | about 29 min, 19–20 GiB |
| Stage-1 behaviour against stage 0 (1,270 programs) | 1,067 same, 188 differ only in live cells at exit, 0 other |
| Stage 2 built by stage 1, defaults (16 partitions, 6 processes) | 620 s, 17.4 GiB |
| Stage 2 as a single module (`ASHES_LLVM_JOBS=1`) | 856 s, 13.5 GiB |
| Stage 3 built by stage 2 | 475 s, byte-identical to stage 2 |
| Stage 3 memory, resident plus swap | 37–48 GiB |
| Semantics suite | 96 of 98 groups (whole-program IR parity 32 of 95 differing) |
| Backend suite | 495 of 497 (one live-cell count case, twice) |
| C# lowered-IR parity tests | 208 of 208 |

- Typed lowering is the only lowering the shipped compiler reaches, with no switch and no fallback.
  The raw lowering survives as a test-only path in `CoreLowering.Raw` until step 9.
- Partitioned multi-process code generation is the default above 2,048 lifted functions.
  `ASHES_LLVM_JOBS=1` selects one module; `ASHES_LLVM_PROCESSES=1` builds partitions in process.

## Known open defects

Wrong output, found by the behaviour check under `ASHES_RC_POISON=1`. The Fix column is as of
round 8; step 2 brings the fixes onto main.

| Defect | Stage | Fix |
| --- | --- | --- |
| A view of a let-bound call result (`uncons`, `unconsText`, `subView`, `fromText`) escaping in a record, tuple or closure reads freed memory | 0 and 1 | On `fix/r12-leak-generic-call`; `fromText` still fails on stage 1 |
| A view matched later, or passed as a loop successor, reads freed memory | 1 | On `fix/s1-view-backing-uaf` |
| A coroutine's captured value returned through its task result is released at frame teardown | 0 | `93754484` |
| `arena_record_result_owned_field_moves_into_copy`, `arena_record_result_read_before_return_moves_field` print 45 for 55 | 1 | Not written |
| `rc_escaping_view_backing_released` prints garbage | 1 | Not written |
| `mixed_tuple_join_taken_apart_into_constructor` segfaults | 1 | Not written |
| A deep copy in a generic helper lowered before its type settles hands the caller's string over uncopied | 0 | Not written (stage 1 is right since step 8 entry 5) |
| `Ashes.Task.Parallel` workers share the caller's libc thread-local storage (`clone` without `CLONE_SETTLS`); a worker that calls into libc corrupts the heap | runtime, both | Step 3 |

Leaks, as known:

- Five C# memory-plateau tests in `LinuxBackendCoverageTests` fail on main; they belong to step 5.
- Test corpus at round 8: 97 programs leave more live cells under stage 1 than under stage 0,
  7.1 million cells in total; with the leak fixes, 36 programs and about 22,600 cells.
- Stage 0 leaks too: a stage-0-built compiler has 221.7 million live reference-counted cells when it
  finishes compiling the compiler. Six leaks present in both compilers are known; five have fixes.
- Stage 1's code generation holds about twice the memory of stage 0's on the same input (2.55 GB
  against 1.2 GB on the frontend test project).
- There is no live-cell count yet for a stage-1-built compiler compiling the compiler; step 2
  produces it.

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
lifetime regression in `tests/`. Shadow counts are agree / differ / unrecorded at `ea730801`; the
unrecorded column of 6, 8 and 11 is not broken down, so their differ counts are a lower bound.

| Entry | Rules | Replacement | State | Corpus, 1,149 programs | Whole compiler |
| --- | --- | --- | --- | --- | --- |
| 1 | `emitDeferredCoreAdd`, `emitDeferredCoreEquality` | an open operand is an error, never the integer form | Done | | |
| 2 | `unprovenTraitGoalKey`, `openDictionaryGoalKey`, `pinProvenTraitGoal`, `pinProvenEqualityGoal` | an open goal is refused at its site | Done | | |
| 3 | `activeEvidenceCoversOperator`, `traitPlanLacksActiveEvidence` | dispatch from the recorded operator instance | Done | | |
| 5 | `lowerInternalDeepCopy` | the copy at the value's recorded type | Done | | |
| 4 | `bodyEncounteredRequirementClosedScope` | recorded scope types from the start; reads parameters at entry, so it waits for 11 | Deferred | 0 / 8,644 / 0, 801 programs | 0 / 2,287 / 0 |
| 6 | `selfResultDeferred` | the binding's recorded result layout decides the arena request | Deferred | 8 / 461 / 2,469, 210 programs | 214 / 1,039 / 31 |
| 7 | `siblingResultOpen`, `siblingResultPinned`, `deferredSelfResultNamedByConsumer`, `keepFirstSelfResultDeferral` | the group member's recorded result layout decides; the largest IR change | Deferred | 0 / 71 / 3, 37 programs | 0 / 1,833 / 50 |
| 8 | `requestsArenaResult`, `containsUnresolvedLayout` | the recorded result type closed by the instantiation; still open keeps the request | Deferred | 27,418 / 546 / 4,865, 203 programs | 44,203 / 146 / 153 |
| 9 | `calleeResultUnsettled` | a callee result summary computed before any body is lowered (first settle whether the result-reach summaries can carry it) | Deferred | 36 / 231 / 0, 228 programs | 109 / 22 / 0 |
| 10 | `lowerFunctionBodyResolvingCalls`, `lowerRecursiveBodyResolvingCalls`, `anyCallResultResolved` | none: with 6 to 9 every body is lowered once | Deferred | 0 / 493 / 0, 243 programs | 0 / 370 / 0 |
| 11 | `normalizesAlwaysReturnedParameter` and the parameter-to-result arena boundary | taken when the recorded type needs it, with the release on every exit first (eleven programs leaked without it) | Deferred | 31,738 / 6 / 9,370, 5 programs | 26,895 / 8 / 2,542 |

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
