# Self-Hosting: Building the Ashes Compiler in Ashes

This document is the plan for replacing the C# compiler with the self-hosted one: one task list, the
current state, and the design reference the next steps need. History is in the
[self-hosting log](SELF_HOSTING_LOG.md) and the item catalogue in the
[self-hosting checklist](SELF_HOSTING_CHECKLIST.md).

## Plan and current state

Last rewritten 2026-11-25. This section is the plan; everything below it is reference. History lives
in [SELF_HOSTING_LOG.md](SELF_HOSTING_LOG.md).

### Goal

Replace the C# compiler (stage 0) with the self-hosted compiler (stage 1). Done means:

- stage 1 builds stage 2, stage 2 builds stage 3, and stage 2 and stage 3 are byte-identical;
- stage 1's behaviour matches stage 0's, diagnostics and execution included;
- typed lowering is the only lowering (steps 7, 8 and 9 of [Type inference before lowering](#type-inference-before-lowering));
- code generation and linking run in parallel;
- no leaks remain, in either compiler.

A successful stage-1 build or a focused test is never a substitute for these gates.

### Order of work

Set on 2026-11-25. Each step ends with a pull request that is merged before the next step starts.

| Step | Work | State |
| --- | --- | --- |
| 1 | Finish round 8 on `selfhost/round11`, fix what breaks, merge PR #1161 | Closing: verify gate running |
| 2 | Bring every leak fix written so far onto the new main (no new leak fixes), measure the build with them, fix only what breaks | Fixes exist on `probe/r12-leaks-b` and the `fix/r12-leak-*` branches; probes V, W, X pending |
| 3 | The parallel runtime bug (one worker) and step 8's deferred entries 4 and 6–11 (another worker) | Not started |
| 4 | Step 9: delete the raw lowering and retire the 258 lowered-IR parity fixtures | Not started |
| 5 | All workers on leaks, highest impact first, measured and merged per batch | Not started; first batch is the known wrong-output bugs |

### Where the compiler stands (round 8, `selfhost/round11`)

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | about 29 min, 19–20 GiB |
| Stage-1 behaviour against stage 0 (1,270 programs) | 1,067 same, 188 differ only in live cells at exit, 0 other |
| Stage 2 built by stage 1, defaults (16 partitions, 6 processes) | 620 s, 17.4 GiB |
| Stage 2 as a single module (`ASHES_LLVM_JOBS=1`) | 856 s, 13.5 GiB |
| Stage 3 built by stage 2 | 475 s, byte-identical to stage 2 |
| Stage 3 memory, resident plus swap | 37–48 GiB |
| Semantics suite | 96 of 98 groups (whole-program IR parity 32 of 95 differing, as in every round) |
| Backend suite | 495 of 497 (one live-cell count case, twice) |
| C# lowered-IR parity tests | 208 of 208 |

What that state contains:

- **Typed lowering is the only lowering the shipped compiler reaches.** No switch, no fallback. The
  raw lowering survives as a test-only path in `CoreLowering.Raw` until step 9.
- **Step 8 entries 1, 2, 3 and 5 are done.** Entries 4 and 6–11 are deferred to step 3 of this plan;
  their sizes are in the step 8 section.
- **Partitioned multi-process code generation is the default** above 2,048 lifted functions.
  `ASHES_LLVM_JOBS=1` selects one module; `ASHES_LLVM_PROCESSES=1` builds partitions in process.

### Known open defects

Wrong output, found by running the behaviour check under `ASHES_RC_POISON=1`. All of these are on
main today; none is fixed on the integration branch.

| Defect | Stage | Fix |
| --- | --- | --- |
| A view of a let-bound call result (`uncons`, `unconsText`, `subView`, `fromText`) escaping in a record, tuple or closure reads freed memory | 0 and 1 | On `fix/r12-leak-generic-call`; `fromText` still fails on stage 1 |
| A view matched later, or passed as a loop successor, reads freed memory | 1 | On `fix/s1-view-backing-uaf` |
| A coroutine's captured value returned through its task result is released at frame teardown | 0 | `93754484` |
| `arena_record_result_owned_field_moves_into_copy`, `arena_record_result_read_before_return_moves_field` print 45 for 55 | 1 | Not written |
| `rc_escaping_view_backing_released` prints garbage | 1 | Not written |
| `mixed_tuple_join_taken_apart_into_constructor` segfaults | 1 | Not written |
| A deep copy in a generic helper lowered before its type settles hands the caller's string over uncopied | 0 | Not written (stage 1 is right since step 8 entry 5) |
| `Ashes.Task.Parallel` workers share the caller's libc thread-local storage; a worker that calls into libc corrupts the heap | runtime, both | Not started; step 3 |

Other open items:

- A loop parameter whose element type stays a free type variable has no layout and is never
  released, in both compilers. Design question: what layout an unconstrained element type gets.
- A stage-1-built compiler prints no `ashes-rc-live` line at exit (it ends through `Ashes.IO.exit`).
  Fixed on `fix/r12-leakcheck-exit`; goes in with step 2.
- The remaining diagnostics where stage 1's text or position differs from stage 0's (about two dozen
  tests).
- Five C# memory-plateau tests in `LinuxBackendCoverageTests` fail on main; they belong to the leak
  work (step 5).

### Leaks: what is known

- **Test corpus, stage 1 against stage 0.** On the integration branch 97 programs leave more live
  cells than stage 0, 7.1 million cells in total. On the leak branch 36 programs and about 22,600
  cells after probe L, and fewer after the later probes once the stage-1 half of the matched-tail
  rule fires.
- **Stage 0 itself leaks.** A stage-0-built compiler has 221.7 million live reference-counted cells
  when it finishes compiling the compiler. Six leaks present in both compilers have been found; five
  have fixes on the leak branch.
- **Stage 1's code generation holds about twice the memory of stage 0's** on the same mid-size input
  (2.55 GB against 1.2 GB on the frontend test project), and stage 3 needs 37–48 GiB where stage 2
  needs 13–17 GiB.
- There is no live-cell count yet for a stage-1-built compiler compiling the compiler. Step 2
  produces it.

### How a round is run

The coordinator runs every build, suite and measurement. Workers implement in their own worktrees
under `.claude/worktrees/`, run quick checks only, commit without attribution trailers, and do not
push; the coordinator pushes every branch.

A round on the integration worktree (`step-f-round3`) is, in order: integrate commits and run the
whole-compiler inference check; stage 1 from stage 0; the REST checks; the behaviour check, once
plainly and once under `ASHES_RC_POISON=1`; stage 2; stage 3 and the byte comparison; the stage-2
behaviour check; the semantics and backend suites; for C# changes the C# build, tests and format
check. Before a pull request merges, `scripts/verify.sh` as well.

Limits that have each cost a day when ignored:

- Stage 3 runs under `MemoryMax=40G MemorySwapMax=32G`; sample `VmRSS` and `VmSwap` together, since
  a resident-only reading looks like memory falling while it is being paged out.
- At most two compiler builds at once on this 60 GiB machine. Anything above 8 GiB starts only when
  35 GiB is available and at most one other compiler build is running.
- Every compiled program runs under a 4 GiB cap; single compiles under 8 GiB.
- A running queue script does not see edits to its file; stop it by PID and relaunch. Never kill by
  name pattern.
- Check a probe's `source.commit` before attributing its rows to a commit.
- Leave a waiter on every build queue; a queued script does not wake the coordinator.
- Never modify a worktree a job is reading.

Environment switches in use: `ASHES_RC_LEAKCHECK` and `ASHES_RC_POISON` (read at compile time),
`ASHES_LLVM_JOBS`, `ASHES_LLVM_PROCESSES`, `ASHES_DIAGNOSTIC_LLVM_STATS=<path>`,
`ASHES_DIAGNOSTIC_IR_PREFIX=<prefix>`, `ASHES_LINK_MAP=<path>`.

## Type inference before lowering

Stage 1 used to infer types while lowering, as stage 0 does. Because lowering met types it did not
know yet, it ran a discovery pass and then replayed the whole program with proven types pinned,
lowered a recursive body again when a call's result type resolved late, and deferred operators and
dictionaries it could not decide at the site. The direction is an inference pass that runs first and
a lowering that reads its types in one pass. Stage 1 does not have to emit the same IR as stage 0
for this: the checks are that programs behave the same under both compilers (output and exit
live-cell counts) and that stage 2 and stage 3 are identical.

### Design

**The inference is stage 1's type checker.** `ProgramInference.ash` and `TypeInference.ash` cover
every expression and pattern form. `compile` and `run`, file and project forms, infer the stitched
program before lowering it. A program it rejects fails with stage 0's code and message
(`InferenceDiagnostics.ash` maps every error the inference raises); the inference also carries the
site stage 0 words a bare unification error after, so no wording comes from lowering. This gives
stage 1 the checks lowering lacks: capability rows, providers, handlers, written requirements and
type alias cycles.

**The node table.** `NodeIdentity.ash` wraps every expression node of the stitched,
deriving-expanded units in `ExprNode(id, node)`, numbered in pre-order. A node lowering creates
after numbering carries a negative identity derived from its source node and a role, with no
counter; readers map it back to the source node. `ProgramInferenceResult.nodeTypes` is a
`NodeTypeTable` of four maps keyed by node id:

| Map | Holds |
| --- | --- |
| `types` | the type of every expression |
| `operators` | trait and operand type per operator |
| `instantiations` | arguments, requirements and dictionaries per call |
| `bindings` | schemes at `let`, lambda and arm-body nodes |

A generic body keeps its binding's quantified variables; a body lowered at a concrete type applies
the instantiation's arguments to the stored type. Over the whole compiler every one of 398,599
nodes has exactly one recorded type.

**What lowering reads from the table:**

- Operator and trait-method operands, dictionaries and `Default` results through their recorded
  types, closed by the enclosing instantiation and by the variables the program's uses settle
  (transitively).
- A partly open equality (`xs == []`) through the recorded type or, in a constrained body, that
  body's dictionary for the whole operand type.
- A call's requirement reduced through the implementations as the inference reduces it
  (`Eq(List(a))` to `Eq(a)`) to match the recorded dictionaries; a requirement at a variable no use
  settles is bound to the body's single dictionary of that trait.
- A bare reference to a constrained binding through the instantiation at its own node.
- Arithmetic-generic bindings used at more than one type, planned as a constrained binding with the
  operator's dictionary, as stage 0 and reference section 21.6 do.
- Evidence dispatch through the recorded operator instance (step 8 entry 3).

A reader takes a record only when it matches its site: an operator record only for the site's own
trait, a dictionary only for a requirement of the same trait that unifies, a scheme only for the
same binding name and value type. Readers of an expression's own record fall back to lowering's own
inference otherwise. An expression built by `lowerSynthesized` has no
current node, so nothing is read for it. Recorded but not read: handler operation arm parameters
(only the `resume` call's type is read), and record construction and update instantiations. Other
synthesized nodes (evidence rewrites, `$partial` captures, constrained-value wrappers, specialized
copies) still keep the identity of the nearest numbered node.

**Schemes and unconstrained variables.** A binding's requirements are reduced through the
implementations as stage 0 reduces them. A written requirement on known types, or one an
implementation head matches, is rejected as unjustified (`ASH010`). A variable nothing constrains
stays open and lowering treats it as any type, never as `Int`. A requirement still open in the
trailing expression is ambiguous (`ASH010`), with no numeric default.

**The lowering entry points.** `CoreLowering` exports only the numbered entry points
(`lowerCoreNumberedProgram*`, `lowerNumberedProgramWithRegionsAndReuse`,
`lowerNumberedProgramWithSourceAndReuse`) and `withSourceRegions`. The raw (un-numbered) entry
points live in `CoreLowering.Raw` and the measured lowering in `CoreLowering.Measurement`; no module
under `selfhost/packages` imports either, only test projects. The machinery the raw entry points
reach is still shared with the typed path in `CoreLowering.Program`, `.Operators`, `.Expressions`
and `.RecordedTypes` (`lowerProgramPasses` takes the discovery and replay branch when the state has
no node table), so it is linked into the compiler but unreachable from its entry points.

**The measurement driver** is `selfhost/tests/typed-lowering-measurement` (one file or the corpus
through `sweep.sh`; see its README). It reports replays, pending goals, deferred operators and
re-lowerings, and per step-8 entry a shadow count: at each site the entry decides, the decision
taken against the one the final recorded type gives, as agree / differ / unrecorded
(`--shadow-details`, `--goal-details`, `--ir-hash`, `--single-lowering`).

### Steps

| Step | Work | State |
| --- | --- | --- |
| 1 | Skip the constrained-binding plan when a syntactic check shows the binding cannot be constrained | Not recorded |
| 2 | Make the standalone inference run on real programs, from the compile command | In place: it is the type checker |
| 3 | The behavioural check: compile the corpus with both stages, compare output and exit live cells | In place: `selfhost/tests/behaviour/check.sh` |
| 4 | Make the standalone inference complete over the language | In place: it type-checks the compiler and the whole test corpus |
| 5 | Compare the standalone inference with lowering's own | Done for top-level schemes and binary operator sites |
| 6 | Give syntax nodes an identity and record types against it | In place (the node table above) |
| 7 | Lower from the table | Exit condition met |
| 8 | Replace the order-dependent rules by rules over the final type | Entries 1, 2, 3, 5 done; 4 and 6–11 deferred |
| 9 | Retire the lowered-IR parity fixtures and delete the raw lowering | Not started |

Steps 1 to 5 do not change generated code; steps 7 and 8 do, in the ownership-sensitive paths.

### Step 7: exit condition

The shipped compiler reaches only the typed lowering, with no switch and no fallback. This is met:
the stitched-program lowering, `ASHES_TYPED_LOWERING` and `ASHES_SKIP_INFERENCE_CHECK` are gone, a
trait goal left under typed lowering is refused at its site, and the corpus sweep (1,148 programs)
and the whole compiler run with 0 replays, 0 pending trait goals, 0 equality goals and 0 deferred
operators. Under typed lowering the per-binding plan and the speculative operator form still run:
the plan decides which bindings are constrained bodies, and the speculative form is reached only
where the table has no answer, which the corpus and the compiler no longer hit. Both are step 8.

### Step 8: the order-dependent rules

Each entry is a lowering decision keyed on whether a type is still open at a site. It is replaced
by a rule over the final type, under typed lowering only; the raw path keeps the old rule until
step 9. Each replacement is shown safe by its shadow count: zero differing sites means the
replacement is IR-identical; otherwise each differing program gets a per-function diff of
`--emit-ir lowered`, the behaviour check with `ASHES_RC_LEAKCHECK=1`, and a lifetime regression in
`tests/` that pins the new placement.

| Entry | Rules | What it decides | Final-type replacement | State |
| --- | --- | --- | --- | --- |
| 1 | `emitDeferredCoreAdd`, `emitDeferredCoreEquality` | `+`, `==`, `!=` over two open operands emit the integer form | the recorded operand type; still open is an error | Done (`d0a78675`) |
| 2 | `unprovenTraitGoalKey`, `openDictionaryGoalKey`, `pinProvenTraitGoal`, `pinProvenEqualityGoal` | an open goal or dictionary is deferred to the replay | the recorded dictionary; a goal left is refused at its site | Done (`80dc9a77`) |
| 3 | `activeEvidenceCoversOperator`, `traitPlanLacksActiveEvidence` | an open operand dispatches through a generic body's evidence only when the body carries it | dispatch from the recorded operator instance | Done (`1f46e580`) |
| 4 | `bodyEncounteredRequirementClosedScope` | a body whose scope types a mapped operator closed is lowered again | the recorded scope types from the start | Deferred: reads parameter types at entry, which switches on entry 11 |
| 5 | `lowerInternalDeepCopy` | a copy of a value of open type passes through and asks for re-lowering | the copy at the value's recorded type | Done (`4176b44c`) |
| 6 | `selfResultDeferred` | a deferred self call asks for an arena result | the binding's recorded result layout decides | Deferred |
| 7 | `siblingResultOpen`, `siblingResultPinned`, `deferredSelfResultNamedByConsumer`, `keepFirstSelfResultDeferral` | arena requests of calls within a recursive group | the member's recorded result layout decides | Deferred |
| 8 | `requestsArenaResult`, `containsUnresolvedLayout` | a call whose result layout is open asks for an arena result | the recorded result type closed by the instantiation | Deferred |
| 9 | `calleeResultUnsettled` | a callee whose body is not lowered yet is asked for an arena result | a result summary computed before any body is lowered | Deferred |
| 10 | `lowerFunctionBodyResolvingCalls`, `lowerRecursiveBodyResolvingCalls`, `anyCallResultResolved` | a body whose call results resolved is lowered again | nothing: with 6 to 9, every body is lowered once | Deferred |
| 11 | `normalizesAlwaysReturnedParameter` and the parameter-to-result arena boundary | taken only when the parameter's type is known at entry | taken when the recorded type needs it, with the release on every exit | Deferred |

Entries 1, 2, 3 and 5 leave the corpus's lowered IR unchanged (same IR hash on all 1,149
programs); entry 5 changes 12 sites in the compiler, deep copies in small generic helpers that were
passed through where the source asks for a copy (corpus test
`deep_copy_in_generic_helper_settled_at_one_type`).

Entries 6 to 9 are stage 1's version of stage 0's own order dependence (stage 0 has the same rules
on its live types: `RequestsArenaResult`, `ContainsUnresolvedLayoutType`, `CalleeResultUnsettled`,
`EmitRuntimeManagedTcoArgumentNormalization`), so replacing them changes generated code by design.
Entry 7 is the largest change: taking those results from the table changed ownership placement in
21 swept programs. Entry 9 first needs to settle whether the existing result-reach summaries can
carry the callee's result fact or a new pass is needed. Entry 11 switched on in eleven programs that
then leaked when parameters were read from the table before the body, so its release comes first.

Shadow counts for the deferred entries at `ea730801` (agree / differ / unrecorded; on the corpus
also the programs with a differing site). The unrecorded column is not broken down for 6, 8 and 11,
so their differ counts are a lower bound:

| Entry | Corpus, 1,149 programs | Whole compiler |
| --- | --- | --- |
| 4 | 0 / 8,644 / 0, 801 programs | 0 / 2,287 / 0 |
| 6 | 8 / 461 / 2,469, 210 programs | 214 / 1,039 / 31 |
| 7 | 0 / 71 / 3, 37 programs | 0 / 1,833 / 50 |
| 8 | 27,418 / 546 / 4,865, 203 programs | 44,203 / 146 / 153 |
| 9 | 36 / 231 / 0, 228 programs | 109 / 22 / 0 |
| 10 | 0 / 493 / 0, 243 programs | 0 / 370 / 0 |
| 11 | 31,738 / 6 / 9,370, 5 programs | 26,895 / 8 / 2,542 |

Planned order: 8 and 9, then 6 and 7 with 10, then 11, and 4 after 11. The per-binding plan
(`constrainedBindingPlan`) is not one of the entries; replacing it by the recorded schemes follows
entry 3, because the plan decides which bodies carry the evidence 3 reads.

Re-lowering cost: the typed lowering of the compiler is one pass of 224 s (inference 80.8 s) with
2,657 body re-lowerings, the sum of entries 4 and 10. What re-lowering costs in time is unmeasured;
`--single-lowering` keeps the first lowering of every body, and its `first_ms` against an ordinary
run is an upper bound on what entries 4, 10 and 11 together save.

One diagnostic waits on this step: `polymorphism_rec_poly_rejected` is reported at the binding
where stage 0 reports the self call, because an unannotated recursive lambda's parameter is still
its own variable when the body's self call is checked.

### Step 9: the raw lowering

Stage 0 stays the bootstrap compiler; stage 1 stops mirroring its internals. Deleted with the
fixtures:

- `CoreLowering.Raw`, all of it: `validateRawBindingEvidence`, `lowerValidatedCoreProgram`,
  `lowerCoreProgram`, `lowerValidatedCoreProgramWithSourceAndContext`,
  `lowerCoreProgramWithSourceAndContext`, `lowerProgramWithRegionsAndSchemes`,
  `lowerProgramWithRegionsAndReuse`, `lowerProgramWithSourceAndReuse`,
  `lowerValidatedCoreProgramWithSchemesAndReuse`, `lowerValidatedCoreProgramWithRegionsAndReuse`,
  `lowerCoreProgramWithRegionsAndReuse`, `lowerCoreProgramWithSourceAndReuse`,
  `lowerCoreProgramWithSource`, `lowerValidatedCoreProgramWithEnvironment`,
  `lowerCoreProgramWithEnvironment`, `lowerCoreExpression`, `lowerCoreExpressionWithLayouts`,
  `lowerCoreExpressionWithContext`, `lowerCoreExpressionWithFullContext`,
  `lowerCoreExpressionWithCompleteContext`, `lowerCoreExpressionLocated`,
  `lowerCoreRecursiveGroup`, `CoreObservedTypes`, `observedScheme`, `observedOperator`,
  `observedTypesOf`, `lowerCoreProgramObservingTypes`.
- `CoreLowering.Measurement`: `measureRawCoreProgramLowering`, the `None` table of `measuredSeed`
  and `measureCoreProgramLoweringObserved`, and the raw branch of `measureStitchedLowering`.
- `CoreLowering.RecordedTypes`: `rawOperatorDeclarationSchemes`,
  `withRawOperatorDeclarationSchemes`, `rawOperatorSchemesWithNestedLets`,
  `rawNestedOperatorGenericScheme`, the `None` branch of `recordedDeferredEvaluation`, and the
  registry's `rawDeclarationSchemes`.
- `CoreLowering.Program`: the replay (`replayProgramWithProofs`, `replayLambdaOrAlias` and the
  replay dependency graph) and the no-table branch of `lowerScheduledProgramPasses`.
- What only the raw path reaches once step 8 is done: discovery, pending and proven goals, deferred
  operators, the per-binding plan and body re-lowering.

What keeps the raw lowering alive today:

- The 258 lowered-IR parity fixtures against stage 0 (`selfhost/parity/semantics/lowered-ir`),
  driven by `LoweredIrFixtures`, `ir-program-parity`, `semantics-diagnostic-parity` and the backend
  raw fixtures. Typed IR differs from stage 0's by design, so these are retired, not regenerated.
- The selfhost semantics tests that import `CoreLowering.Raw` (about thirty files, listed by that
  import), deleted or moved to the numbered entry points.

## How to work on this

Rules, in addition to "How a round is run" above:

- Fix the compiler, never the witness: do not rewrite a test, example or self-hosted source to get
  around a compiler defect.
- A change to how stage 0 lowers ownership ships with its stage-1 mirror and regenerated fixtures
  (`ASHES_UPDATE_PARITY_FIXTURES=1`) in the same pull request; so does any other stage-0 fix the
  compiler's own sources depend on.
- A green IR-text comparison is not a behaviour check. Ownership changes are gated by suites that
  run code stage 1 generated (the behaviour check, the self-hosted `cli` and `backend` suites) and
  by the `challenges/` programs against `main`.
- Leak-free means compiling one more function with stage 1 adds no reference-counted cell still
  live at exit, and resident memory plateaus over a long compile.
- Crashes, use-after-free and over-releases come before leaks.
- Start a behaviour change with a failing pure-Ashes test under `selfhost/tests/<package>/`; keep
  unit tests in the owning package and compose checks through pipelines, not nested `let ... in`.
- Treat stage-0 comments as audit input when porting: keep non-obvious behaviour, ordering,
  ownership, diagnostics and phase boundaries in a module header or a local comment; do not copy C#
  API narration.
- Keep the .NET implementation buildable and tested as the permanent stage-0 and behavioural
  reference; the .NET registry server is a deployed service, so only its client commands are ported.
- No development tags; never touch `main`'s history or delete a `release/*` branch.
- When a checklist item is finished, remove it from [the checklist](SELF_HOSTING_CHECKLIST.md) and
  name it in the log entry of the pull request that finished it. Identifiers are never reused.
  Checklist items stay short: ID, scope, a "Done:" and an "Open:" clause, at most one gotcha.
- On the development machine, read `.claude/worktrees/step-f-round3/work/coord/rules.md` before
  running a compiler. Copy `runtimes/` into a new worktree before backend-dependent checks.

Recipes:

| Task | How |
| --- | --- |
| Behaviour check | `selfhost/tests/behaviour/check.sh` against `selfhost/tests/behaviour/baseline.tsv`; stage 1 has no `test` command, so a project test runs through `compile --project` |
| Self-hosted suites | `scripts/selfhost-dev/suites.sh`, `--focused` for the fixtures; run semantics tests also with `--debug-disable-reuse` |
| Stage-1 build for a round | `--debug -O2` with `ASHES_RC_LEAKCHECK=1`; stage 2 with its link map (`ASHES_LINK_MAP`) |
| Locate a crash in a stage-1-built binary | `selfhost/tests/behaviour/crash-trace.sh --map` against the link map |
| Find a leak | Compile a tiny input with stage 1, take a census of what is live at exit, add one construct and diff the census; then put a hardware watchpoint on one leaked cell's reference count and find the retain with no release. Probe a frozen copy of the packages: editing `selfhost/` moves every address |
| Memory that does not fit | Count allocations before assuming a missing release: identical objects in power-of-two multiplicities are repeated work |
| Where LLVM time and memory go | `ASHES_DIAGNOSTIC_LLVM_STATS=<path>`: one line per LLVM function, a phase clock and the partition timings |
| Sample a running compiler under gdb | Start it as gdb's child; ptrace scope 1 forbids attaching |
| Bisect a benchmark regression | Publish the compiler at a commit (`publishcli.sh`, or `publishref.sh` for one not checked out) and compare with `challenge_ab.sh` |

## Reference

- [Self-hosting log](SELF_HOSTING_LOG.md): what each merged pull request and round changed.
- [Self-hosting checklist](SELF_HOSTING_CHECKLIST.md): the item catalogue (OPT-, CG-, LNK-, BOOT- and
  the other IDs) with each item's recorded state.
- [Archive of 2026-11-25](https://github.com/MattiasHognas/Ashes/tree/main/docs/selfhost-archive/2026-11-25):
  this plan's earlier text (the lettered work order A to T, every round note, the known-gaps table,
  the migration table) and the log before it became a changelog.
- [Self-hosted toolchain README](https://github.com/MattiasHognas/Ashes/blob/main/selfhost/README.md):
  packages, their dependency graph, commands and implementation status.
- [Development tools](https://github.com/MattiasHognas/Ashes/blob/main/scripts/selfhost-dev/README.md):
  the scripts the rounds use.
- [Language reference](../reference/language.md), [diagnostics](../reference/diagnostics.md),
  [architecture](../internals/architecture.md), [testing](../guide/testing.md).
