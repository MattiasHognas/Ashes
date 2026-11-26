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
- no leaks remain, in either compiler;
- the self-hosted compiler compiles faster than the C# compiler.

A successful stage-1 build or a focused test is never a substitute for these gates.

## Tasks

This is the only task list. Each task ends with a pull request that is merged before the next
starts.

### Current

| # | Task | Done when | State |
| --- | --- | --- | --- |
| 1 | Finish round 8 on `selfhost/round11`, fix what breaks, merge PR #1161 | PR #1161 is merged | Done: merged as `3432916f` |
| 2 | Bring every leak fix written so far onto main (no new leak fixes), measure the build with them, fix only what breaks | The fixes are on main and a round with them passes every check in "How a round is run" | Done: merged from `selfhost/round12` after round 12. Left out: the pass-through loop-parameter placement (it leaked 18,000 cells on one test). Merged but not yet effective in stage 1: the matched-tail rule and the self-call hand-over |
| 3 | Give `Ashes.Task.Parallel` workers their own libc thread-local storage; replace step 8's rules that pay for themselves | A worker that calls into libc no longer corrupts the heap; no function body is lowered twice | Parallel runtime done. Step 8 entries 1–3, 5–9 and the copy-out port (8c) done. Remaining here: the double lowering of function bodies (entries 11 and 4), about half of lowering time |
| 4 | All workers on leaks, highest impact first, measured and merged per batch: first the known wrong-output bugs, then what the exit census of a stage-1-built compiler shows by type and allocation site, then the test rows | The behaviour check reports no program differing in live cells, plain and under `ASHES_RC_POISON=1`, and a compiler built by either stage leaves no live cell after compiling the compiler | Not started |

### Next

| # | Task | Done when | State |
| --- | --- | --- | --- |
| 5 | The step 8 rules skipped for now (table below): entry 10's remaining re-lowerings, results closed only by a generic body's single use (8b), copy-out results inside coroutine bodies and other heap shapes (8c remainder), general reference-counted self results in loop bodies | Every row of the step 8 table is Done or records why it stays | Not started |
| 6 | Step 9: delete the raw lowering and retire the 258 lowered-IR parity fixtures | `CoreLowering.Raw` and everything only it reaches are gone, with the fixtures and their harnesses | Not started; needs task 5 |
| 7 | Close the remaining stage-1/stage-0 differences: the diagnostics whose text or position differs, and the 32 of 95 whole-program IR parity fixtures if step 9 has not retired them | The behaviour check, plain and under `ASHES_RC_POISON=1`, reports every program as same, and no compile-error test prints a different diagnostic | Not started |
| 8 | CLI parity: port the commands `test`, `repl`, `login`, `publish`, `yank`, `search` and `info`, the options `--target`, `--target-cpu`, `-O0`–`-O3`, `--expr`, `--emit-ir`, `--parallel-workers` and `--parallel-stack-size`, and `restore` beyond path dependencies (`Dispatch.ash` and `Compile.ash` in `selfhost/packages/cli` against [the CLI reference](../reference/cli.md)) | Every command and flag in the CLI reference works from the self-hosted binary, or the reference says it is not provided | Not started |
| 9 | Port linux-arm64, win-x64 and win-arm64 code generation and linking (`Compile.ash` hard-codes `x86_64-unknown-linux-gnu`; the linker writes ELF only) | Binaries built by the self-hosted compiler pass the cross-target validation the C# compiler passes: qemu for linux-arm64, wine for win-x64, structural checks for win-arm64 | Not started |
| 10 | Debug information: emit DWARF and accept `--debug`/`-g` (the self-hosted backend has no debug-info builder) | The [debugging guide](../guide/debugging.md)'s workflow works on a self-hosted build | Not started |
| 11 | Replace every .NET tool with one built in Ashes on the self-hosted compiler: the test runner (`src/Ashes.TestRunner`), the language server (`src/Ashes.Lsp`), the debug adapter (`src/Ashes.Dap`), the fuzzers (`src/Ashes.Fuzzing`), the registry server (`src/Ashes.Registry`) and the API docs generator (`tools/docs-api-gen`); port or replace their C# test suites as each moves | Each tool runs from Ashes sources with its tests, the VS Code extension and the docs build use the Ashes-built ones, and nothing in the default build needs .NET | Formatter done (`selfhost/packages/formatter`, wired to `fmt`); the rest not started |
| 12 | Performance and memory: the self-hosted compiler must compile faster than the C# compiler on every target program, and stage 3 must build within stage 2's memory. Known items: heap results with no copy-out keep the caller's arena window open (about 2,500 call sites in the compiler, 370 in loop bodies; a copy rule beyond stage 0), and the borrowed-result copy (a `Str` or `Bytes` returned out of a function from borrowed data is copied in both compilers, where only the trivial field accessor retains it) | The self-hosted compiler builds itself in under 25 s and under 2 GiB (what `dotnet build` of the C# compiler takes: about 25 s, 1.9 GiB), is faster than the C# compiler on ordinary programs, and stage 3 builds within stage 2's memory; the gate is recorded in this document | Not started. Today the self-hosted compiler builds itself in 561 s at 18 GiB (about 60 to 80 s of that is LLVM on six processes); stage 0 takes 27 min at 20 GiB for the same source. Likely needs a per-module cache and an unoptimized development mode besides faster phases |
| 13 | Decide what layout an unconstrained element type gets (a loop parameter whose element type stays a free type variable is never released, in both compilers) | The language reference states the rule and both compilers implement it | Not started |
| 14 | Make the self-hosted compiler the default toolchain; keep the C# compiler, its tools and its tests in the repository as an archive | Tasks 4, 11 and 12 are done (no leaks, every tool built in Ashes, and faster than the C# compiler); releases for all four targets are built by the self-hosted compiler from a pinned, reproducible stage-0 input; CI builds, bootstraps and checks it (no job in `ci/` or `.github/` mentions `selfhost` today); the C# compiler, tools and tests are moved to an archive location and no longer part of the default build | Not started; far off |

## Where the compiler stands

Measured at round 15: main with the copy-out port.

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | 27 min, 20 GiB |
| Stage-1 behaviour against stage 0 (1,294 programs) | 1,172 same, 122 differ only in live cells at exit, 0 other |
| The same under `ASHES_RC_POISON=1` | 1,166 same, 122 live cells only, 6 wrong output or crash (below) |
| Stage 2 built by stage 1, defaults (16 partitions, 6 processes) | 605 s, 18.2 GiB |
| Stage 2 as a single module (`ASHES_LLVM_JOBS=1`) | 875 s, 14.7 GiB |
| Stage 3 built by stage 2 | 601 s, byte-identical to stage 2 |
| Stage 3 memory, resident plus swap | 25.0 GiB (40.4 GiB before step 8 stopped arena requests and copy-outs from pinning the caller's arena window) |
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
| Programs built by the self-hosted compiler are dynamically linked against `libc.so.6` as soon as they allocate (`malloc`, `free`, and LLVM's `memcpy`, `memset`, `bcmp`); the C# compiler emits the same programs fully static, which is the language's zero-dependency promise | 1 | Not written |

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

Each entry replaces a lowering decision keyed on whether a type is still open at a site by a rule
over the recorded final type. Counts are shadow sites (driver `--shadow-details`, see its README),
compiler unless marked; each change gets a lowered-IR diff, the behaviour check with
`ASHES_RC_LEAKCHECK=1` and a regression test.

| Entry | Decision | Replacement | State |
| --- | --- | --- | --- |
| 1 | `emitDeferredCoreAdd`, `emitDeferredCoreEquality` | an open operand is refused, never the integer form | Done |
| 2 | `unprovenTraitGoalKey`, `openDictionaryGoalKey`, `pinProvenTraitGoal`, `pinProvenEqualityGoal` | an open goal is refused at its site | Done |
| 3 | `activeEvidenceCoversOperator`, `traitPlanLacksActiveEvidence` | dispatch from the recorded operator instance | Done |
| 5 | `lowerInternalDeepCopy` | the copy at the recorded type | Done |
| 8 | `requestsArenaResult`, `containsUnresolvedLayout` | the call result pinned to its recorded type (closed by the body's instantiation) before the request; an unknown closure keeps its request except for a general reference-counted result (stage 0's `RequestsArenaResultFromUnknownCallee`) | Done (#1163; stage 3 40.4 to 32.4 GiB) |
| 9 | `calleeResultUnsettled` | none: every differing site (22; corpus 237) is a self call, already callee-first | Closed |
| 6 | `selfResultDeferred` | a deferred self call's scalar or general reference-counted result pinned; inside a loop body the general one keeps its deferral (21 sites; corpus 3) | Done (#1164) |
| 7 | `siblingResultOpen` and the pinned and proven sibling results | a group member's scalar or general reference-counted result settled from the table (1,471 sibling and 659 group-member self sites); re-lowerings 2,635 to 2,418 | Done (#1164) |
| 8c | a group-member call's copy-out result left in its window (47 sites, 31 in loop bodies) | the call reads the callee's returns bit on the type its consumer names, as stage 0's pruned arrow does, and copies the result out on the arena branch (`LowerCallConditionalCopyOutResult`); an unconditional copy (`LowerCallCopyOutResult`) where no bit is read. Compiler sites left open 47 to 0 | In review |
| 8c-async | the same in a coroutine body (corpus 42 sites, the `async_*` programs) | needs the copy's release across an await first | Later |
| 8c-other | self and sibling copy-out and other-shape results keeping their request (copy-out 148 + 229, other 23 + 45; unknown closures 7 + 4) | stage 0's post-call copy-out per kind | Later |
| 8d | about 2,500 heap results with no copy-out whose window stays open, as in stage 0 (`LowerUncoveredCallResultCopyOut`; about 370 in loop bodies) | a copy-plan rule beyond stage 0 | Not planned |
| 8b | results closed only by the variables a generic body's single use settles (128; corpus 560) | the same pin, beyond stage 0 | Deferred |
| 11 | `normalizesAlwaysReturnedParameter` and the parameter-to-result arena boundary | decided on the parameter type the lambda's node records (9; corpus 6 programs change IR). A `\|?>` mapper call passes no ownership word, so a mapper normalized this way keeps its payload's reference: that call must hand the payload over first | Held |
| 4 | `bodyEncounteredRequirementClosedScope` | scope types from the table at entry, no closed-scope re-lowering (2,323 re-lowerings, 55% of the lowering time with entry 10). 1,480 of them have no node in effect at entry (parameter lambdas of a `let`), and closing types at entry moves entry 11's decision on generic library bodies | Needs the declaration node |
| 10 | `lowerFunctionBodyResolvingCalls`, `anyCallResultResolved` | none once 4 and 6 to 9 hold (95 re-lowerings left) | After 4 |

Order: 8c, 11, 4, 10, then 8c-async and 8c-other; 8b and 8d go beyond stage 0 and wait.

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
