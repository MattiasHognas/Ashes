# Perceus Ownership Unification — Remaining Implementation Plan

Status: in progress.

Audited against `main` at `a955eea` on 2026-08-02, together with the task-frame teardown and the
first async-ownership narrowing in this change. This document is intentionally a remaining-work backlog.
Completed implementation history belongs in
[`docs/md/internals/changelog.md`](../internals/changelog.md), especially the RC Perceus chronology,
and is repeated here only when it constrains unfinished work.

## 1. Goal and boundaries

The primary goal is to make Perceus the single semantic ownership model for ordinary immutable heap
values:

- every ordinary heap reference has an understood owner;
- sharing creates an explicit `RcDup`;
- the final use creates an explicit `RcDrop`;
- uniqueness and reuse legality come from the same ownership facts;
- arena, stack, runtime RC, copy-out, and in-place reuse are downstream storage/representation
  choices, not competing ownership systems.

The secondary goal is maintainability: compute each ownership fact once, give it an explicit name and
identity, and make lowering/codegen consume it instead of reconstructing it from AST shape, source
names, ambient booleans, or emitted-instruction scans.

This is internal compiler work. It must not add ownership syntax, weaken immutability, or change
source semantics. Resources (`File`, `Socket`, `Process`), scheduler/native state, and foreign
allocations retain their existing deterministic cleanup rules. All four supported target RIDs remain
release gates.

### One model does not mean one boolean

The old version of this document sometimes described the TCO work as collapsing classifiers A, B, C,
and D into “one per-parameter decision.” Current code proves that framing is too coarse:

- semantic ownership is per binding/value: borrow, consume, reach, share, drop, uniqueness;
- RC representability is per resolved type/layout: whether codegen can copy and structurally drop it;
- placement is per produced value/temp: arena, stack, or runtime RC;
- TCO reset/copy-out profitability is per parameter and per back edge.

These facts should share one semantic ownership source, but they must remain separately named facts at
their natural granularity. In particular:

- classifier B (`GetTcoCopyOutKind`/`TcoBackEdge*`) is a downstream storage/reset query;
- classifier C (`IsRuntimeManagedResultTemp`) is a concrete lowered-temp representation query;
- neither can be replaced by a precomputed per-parameter boolean without losing information.

The target is therefore one source of truth for each fact, with explicit hand-offs between ownership,
layout, and placement.

### Follow-on observability contract

The next compiler feature after this migration is a first-class `--explain` facility for ownership,
final Perceus IR, reuse, and physical representation. Implementing that CLI, its formatter, and its
command integration is **not** part of this migration. The migration must, however, avoid leaving the
same decisions trapped in new ambient booleans, mutable dictionaries, or debug strings.

The current repository has three temporary ownership-debug paths:

- `Lowering.MoveAnalysis.cs` formats `FunctionOwnershipSummary` through
  `FormatOwnershipSummaries` and reads `ASHES_EXPLAIN_OWNERSHIP`;
- `PerceusLifetimePlacement` reads the same environment variable and writes placement counts directly
  to `Console.Error`;
- `Lowering.OwnershipShadowCompare.cs` reads the variable and writes disagreement prose directly to
  `Console.Error`.

Every remaining milestone must therefore preserve the following handoff:

- semantic passes expose immutable fact/decision records; they do not add new environment-variable
  reads or direct console output;
- `FunctionOwnershipSummary` carries a stable `SourceFunctionOrigin`, and each production
  `IrFunction` carries an `IrFunctionOrigin` with its generated label, generated kind, source lineage
  or typed compiler owner, and available source location; later report filtering must use the source
  identity rather than require generated labels;
- decisions with several conservative outcomes carry a stable enum/code and the specific facts
  consumed by the decision, not only a final boolean or a human sentence;
- `IrInst.Location` is preserved when instructions are copied or rewritten, and generated operations
  retain enough origin information to correlate them with a source function/value without parsing
  LLVM symbols;
- lowering makes ownership, reuse, and representation decisions retrievable without exposing its
  mutable working dictionaries or retaining full AST graphs solely for reporting;
- the final `IrProgram` after `IrOptimizer.Optimize` remains the canonical source for the later RC
  report. Today `CompileToImage` in `Ashes.Cli/Program.cs` has exactly this pre-LLVM observation point;
  do not move final RC reporting into the LLVM backend.

The existing environment-variable output may remain temporarily as migration validation plumbing,
but no new decision may depend on whether it is enabled. Its removal/deprecation, CLI parsing,
filtering, deterministic human formatting, and stderr routing belong to the follow-on `--explain`
work. This migration supplies structured facts and stable correlation metadata only.

## 2. Implemented foundation that remaining work may rely on

The following is already implemented and is not part of the backlog:

- the runtime RC header/free-list allocation path, `RcDup`, `RcDrop`, `DropReuse`, structural droppers,
  arena allocation, copy-out, and static/in-place reuse;
- pattern-derived ownership facts keyed by the exact `Pattern.Var` syntax node before lowering and
  transported to the binder's distinct local slot during emission. Lexical reference resolution
  prevents same-named binders in sibling arms, nested matches, lets, lambdas, and handler arms from
  sharing an ownership verdict. Embedded and independently escaping bindings enter ordinary Perceus
  ownership immediately; finalization selects runtime-RC or erased markers from the root parameter's
  resolved placement. The ordinary lifetime pass places their final drop, while unchanged transfer
  to the same TCO parameter remains covered by that parameter's owner. The former direct-pattern
  escape re-walk, pending alias chain, and TCO-specific alias activation path have been deleted;
- `PerceusLifetimePlacement`, which moves ordinary-value lifetime markers to CFG-aware last uses while
  leaving resource cleanup alone;
- `FunctionOwnershipSummary` with parameter borrow/consume facts, uniqueness, result reach,
  `ExpressionFreshness`, `FunctionResultProvenance`, and positional `TcoParamFacts`;
- builtin fresh-result metadata (`BuiltinRegistry.BuiltinModuleMember.ProducesFreshRcResult`) for
  owned `Str`, `Bytes`, and `BigInt` producers, including use by interprocedural result provenance;
- shared top-cell freshness traversal for ADTs, tuples, and lists, including mixed-arm
  reconciliation;
- closure/function-result provenance, call-result freshness, and match-scrutinee ownership fixes;
- dynamic capability ownership placement is scoped by an identity-correct per-function “may execute
  under a live handler post” effect. Direct handler owners seed the effect, known calls propagate it,
  and unresolved calls conservatively include escaped functions. The immutable
  `FunctionOwnershipSummary` retains the effect, lowering consumes it through an explicit placement
  context, and `BeginLivePostsGuard`/`LivePostsIndex` still protect values that can cross a pending
  post;
- TCO type-shape predicate deduplication, the shared A/C query at the proven joint sites, and
  profitability-gated frame demotion;
- `TcoSelfCallArgumentShape.GrownCons` and complete expression-freshness recording for self-call
  arguments;
- `TcoParamStructuralFacts.ArenaSelfContainedListRebuild`, computed across every exact self-call
  argument independently of `ExpressionFreshness`. It preserves the arena/reset question answered
  by `IsArenaSelfContainedListRebuildExpr`: a helper-call result can be self-contained relative to
  the callee arena even when it retains an input tail and is therefore not reference-fresh, while a
  direct `head :: oldAccumulator` remains rejected. The fact stays separate from the
  reference-fresh `FreshRebuilt` shape and is the live source for
  `TcoParamStaticFacts.FreshRebuiltList`; the concrete per-edge reset classifier still rechecks the
  actual lowered argument;
- `TcoParamStructuralFacts.FreshClosureRebuild`, computed across every exact self-call independently
  of `ExpressionFreshness`. A new closure can capture an input reference and therefore retain
  `Shape = Mixed`; the separate boolean records only that every relevant edge directly constructs a
  closure or selects between direct constructions. It is the live source for
  `TcoParamStaticFacts.FreshClosureRebuild`, combined with the resolved `TFun` gate and the existing
  per-edge producer/capture-safety checks. A closure first resolved at post-body refresh receives its
  active local there, while its inactive initialization is spliced back into the one-time TCO entry
  prologue. Exit and deferred back-edge consumers consult the explicit placement and tolerate an
  absent active local conservatively;
- positional `TcoParamStructuralFacts` identity. Facts retain both the parameter ordinal used by
  consumers and the source name used for diagnostics, so duplicate curried parameter names remain
  distinct in the immutable summary. `TcoParamFactsWalk` resolves live value names to those ordinals
  across let and pattern scopes. `TcoParamStaticFacts.LoopInvariant`, `FreshRebuiltList`,
  `FreshClosureRebuild`, `AffineConsList`, and `ConsumedListTail` now consume
  `UnchangedPassthrough`, `ArenaSelfContainedListRebuild`, `FreshClosureRebuild`, `GrownCons`, and
  `ConsumedTail` through that boundary; the
  superseded `CollectLoopInvariantParams`, `CollectFreshRebuiltListParams`,
  `CollectFreshClosureParams`, `CollectAffineConsListParams`, and `CollectConsumedListTailParams`
  walks have been deleted, together with the now-unused shared collector and TCO structural shadow
  comparator. The orthogonal `TcoParamUseMode.BorrowInspectOnly` fact now records whether a
  consumed-tail parameter and every head/tail reference derived from it are used only for structural
  inspection or transferred to the same parameter of an exact self-call. Its lexical taint
  environment replaces bindings at let/pattern boundaries, so disjoint same-named binders cannot
  inherit ownership. The self-transfer exception requires both the recursive binding's source name
  and the same lexical `FuncKey`, including a same-named immutable rebinding of the exact recursive
  function while excluding same-named aliases of other functions and differently named aliases;
  match guards participate in the escape proof. Lowering consumes this use mode by parameter
  ordinal, and the superseded `CollectBorrowableConsumedListParams` walk has been deleted. TCO now
  records the curried parameter identity by ordinal and assigns every ordinal a distinct back-edge
  slot. The last, lexically visible same-named binding retains its real local slot; earlier shadowed
  occurrences retain non-participating positional slots, so a positive fact neither fails closed
  merely because a name is duplicated nor attaches to the wrong binding. Parameter labels and
  deferred runtime-argument decisions use ordinal/slot identity instead of a first-name lookup;
- `TcoParamReuseAffinity.SelfAppendOnly`, which records by parameter ordinal that every
  loop-continuing path consumes the parameter only as the leftmost leaf of its own exact self-call
  addition chain, or passes it through unchanged. Exit-path uses remain unrestricted. Lowering
  combines this canonical affinity with the loop-entry watermark to arm `ConcatStrTip`, allocates
  reservation locals in parameter order, and keys them by the distinct parameter slot. The
  source-name-keyed `CollectAffineAccumulators` walk has been deleted;
- immutable `TcoParamStaticFacts` are separate from mutable placement orchestration.
  `TcoParamPlacementDecision` records the parameter ordinal/slot, resolution point, arena or
  runtime-RC representation, stable eligibility/restriction reason, ownership-shape and resolved
  layout inputs, dynamic-boundary restriction, frame-profitability verdict and blocker, transition,
  and first promotion stage. One evaluator runs at provisional loop entry, each resolved back edge,
  and post-body refresh. Its normalized frame-blocking query replaces the former scope/back-edge
  copies, and classifier B consumes the resulting placement decision while retaining its concrete
  per-edge reset checks. Final immutable per-function traces retain both current and superseded
  decisions in parameter order for the later compiler-report snapshot. A later unresolved view
  retains the last accepted concrete runtime type rather than corrupting codegen with weaker evidence;
- `FuncKey` identity and re-keying of the seven main move-analysis tables, with genuine lexical scope
  resolution in `TcoParamFactsWalk`, `CollectCallsAndEscapes`, `ResultReach`, move analysis, and
  result provenance; function-body and per-call-argument scopes follow sequential let/letrec rules,
  respect lambda/pattern shadowing, preserve partial-application completion scopes, and map
  module-alias-normalized binder copies back to the registered identity. Result provenance also keeps
  immutable per-arm alias/scope snapshots and distinguishes exact recursive identities;
- every lambda-valued binding remains registered even when source names collide. `_maNameIndex` is
  only a globally-unambiguous compatibility index; normalized copied binders map explicitly back to
  their original identity, and lowering uses exact label/TCO identities where they are available;
- conservative escape state is keyed by exact `FuncKey`. Each summary exposes an immutable
  `FunctionCallCensus`, per-parameter `ParameterMoveSafetyProof` values, and
  `FunctionResultReachFacts` with stable flags for escape/incomplete or ambiguous census,
  move-linearity, capture, transitive/seed failure, global or unmodelled reach, internal sharing, and
  an explicit conservative-unknown fallback. The positive `UniqueParameters`, `ResultFresh`, and
  `ResultPoisoned` projections remain available while consumers migrate. Poisoned call summaries
  remain fail-closed while still substituting any known parameter reach into their callers;
- lowering retains immutable `OwnershipFactConsumption` records at the existing reuse entry-copy and
  runtime-managed call-result placement decisions. Each record identifies the reportable source
  function, decision, relevant parameter, evaluated and positive facts (including the concrete
  runtime-manageable result-type predicate where applicable), and outcome without exposing the
  mutable analysis tables;
- reuse specialization generation and `IsFullyReusing` reset-safety qualification retain immutable
  `ReuseDecision` records in deterministic source/function order. Each record carries the generated
  function's `IrFunctionOrigin`, reuse-root parameter, related recursive generated label, source
  location, stable outcome, and a concrete acceptance or rejection reason. The former
  `GetOrCreateReuseSpecializationDebugDump` console and `/tmp` IR-dump path has been deleted;
- direct and specialization entry-copy decisions are retained only after structural reuse is known,
  so the record distinguishes a copy retained after a conservative move-safety result, a copy elided
  by a positive move-safety proof, and a direct copy omitted because no structural reuse survived.
  The decision retains the mechanism, source parameter, local slot and location, plus the exact
  `ParameterMoveSafetyCause` flags. Reuse-token production also records whether the candidate is
  statically unique or whether `DropReuse.RuntimeManaged` requires a runtime uniqueness check,
  retaining the source value, IR temp, function origin, and pattern location;
- registered, saturated reuse-specialization calls that are not routed now retain the caller and
  target function, candidate value or call-result source, local slot when available, call location,
  and the first concrete failed gate: uniqueness, fresh-result proof, callee binding, supported fresh
  accumulator layout, or result/accumulator shape. Ordinary recursive self-calls are not reported as
  rejected external opportunities;
- every live reuse token compared by `TryConsumeReuseToken` now retains an accepted or rejected
  constructor-layout decision. The record includes the token source and temp, allocation site and
  target constructor, producer/requested field counts, list-cell identities, runtime-management
  regimes, and the exact field-count, cell-kind, or runtime-eligibility mismatch;
- each `DropReuse` now retains its source value and produced token, and every token has one final
  consumed, released, discarded, or profitability-reverted disposition. `AllocReusing` records
  correlate the token with their result temp and constructor. Compile-time arena/runtime-RC/to-space
  fallbacks and the runtime null-token fallback retain distinct outcomes and reasons; the direct
  reader profitability rewrite also replaces tentative lifecycle facts with its final fresh-allocation
  decision;
- `RecursiveGroupExpr` members use group-plus-ordinal `FuncKey` identities, are registered before any
  member body is analyzed, and share one complete sibling scope. Declarations after a group remain
  visible to analysis, and original member labels plus mutual-TCO wrapper labels map back to the same
  source ownership summary;
- `FunctionResultProvenance` is computed by a whole-program SCC-aware monotone fixpoint over exact
  `FuncKey` forwarding edges. A mutually-recursive component is RC-eligible only when every considered
  terminal result is either an independently eligible construction or a saturated forward to another
  admitted function, and the component is grounded by a reachable independently eligible result.
  Saturated exact self-recursive arms are neutral: they neither establish nor reject eligibility. Pure
  forwarding cycles, parameter results, unresolved calls, and unmodelled terminal results therefore
  remain fail-closed. `ForwardsTo` retains one immediate target only when that function has exactly one
  exact forwarding target; it is not synthesized from an SCC representative;
- `SourceFunctionOrigin` is the stable ownership-report boundary: source and module-qualified names
  where known, declaration location, and deterministic combined-source offset. Project stitching
  retains original names while rewriting module bindings;
- `IrFunctionOrigin` records every production function's unique generated label and typed generated
  kind. Source-derived artifacts retain their source identity and immediate generated parent, while
  shared synthesized functions use typed program, type, external, runtime-layout, or
  mutual-recursion-group owners. Stable discriminators and generation locations distinguish
  generated sites without exposing `FuncKey`, AST identity, object hashes, or traversal order;
- lowering attaches origins at function creation for ordinary/closure functions, reuse and parallel
  specializations, mutual-recursion dispatchers and wrappers, coroutines, external thunks,
  closure-layout normalizers, structural droppers, and deep-copy helpers. Record-copy IR transforms
  preserve this metadata, and the LLVM backend deliberately ignores it.
- lowering maintains one canonical, frame-local `LoweredTempOwnershipFact` per relevant value temp.
  The immutable fact records representation, owner/source identity, resolved type and outer layout,
  drop capability, ownership transition, function/source origin, and a stable reason. Central
  emission registers every ordinary instruction carrying a `RuntimeManaged` result; a mechanical
  test makes new producers opt into that contract. `Borrow`, `RcDup`, borrowed `Bytes` views,
  control-flow joins, known and unknown calls, copy-out/runtime normalization, TCO installation,
  frame restoration, deferred rewrites, and synthesized-frame swaps preserve or refine the fact.
  `IsRuntimeManagedResultTemp` is now a constant-time fact lookup; the parallel
  `_runtimeManagedResultTemps` set and emitted-instruction scans have been deleted.
- expression lowering returns an immutable `LoweredValue` containing the temp, pruned type, and its
  canonical `LoweredTempOwnershipFact` snapshot. Every expression result has a fact, including
  ordinary values whose representation remains conservatively unknown, while legacy leaf emitters
  can still use the temporary pair adapter without losing the ownership-aware expression boundary;
- runtime-RC production is requested through an explicit `LoweredValueRequest`. Its
  `ConsumerCanOwn` proof is distinct from the requested physical representation, and the returned
  temp fact records what lowering actually emitted. The request carries the narrow list-tail,
  constructor-child, and reuse-layout context needed by producers without retaining an `Expr` graph;
- strings, `BigInt`, owned `Bytes`, ADTs, records, tuples, lists, closures, call results, TCO
  arguments, and coroutine expression handoffs now use that explicit request/result path. Deferred
  overloaded addition retains its requested representation until type resolution;
- all `_runtimeRc*AllocationRequested` fields and the related ambient list-tail and ADT-child request
  fields have been deleted. Top-cell freshness remains a separate syntactic input to the explicit
  placement request.
- `OrdinaryHeapLayoutCapability` is the cycle-guarded boundary for resolved ordinary heap layouts.
  It retains structural-copy support, recursive drop support, constructor-specific child offsets and
  drop kinds, outer-cell runtime-reuse support, and stable resource/borrowed-view, unsupported-child,
  unresolved-type, and unsupported-reuse rejection categories. The capability is snapshotted onto
  lowered-temp ownership facts; tuple/ADT drops, TCO child copies, and reuse cleanup consume the same
  descriptor while expression freshness and TCO profitability remain separate policies.
- pattern-extracted references now receive an immutable `PatternBindingOwnershipFact` in the canonical
  function ownership summary. Exact binder identity is transported to the emitted local slot, while
  the retained reportable fact uses a stable per-function binding ordinal, root-parameter ordinal,
  parent-binding lineage, extraction depth, source location, and typed use/classification enums. The
  analysis distinguishes structural or ordinary-call borrows, unchanged transfer to the same exact
  TCO parameter, embedding in a new owner, independent escape and closure capture, and a conservative
  unknown fallback. Lowering consumes that fact directly to create stable binding owners and retains
  the final borrowed, same-parameter transfer, copy-type, arena-erased, or runtime-RC placement
  outcome. Same-named binders in disjoint arms remain distinct by binder, ordinal, and local slot.
- coroutine lifetime markers are placed on the linear pre-transform body, immediately before
  `StateMachineTransform.Transform`, where the `AwaitTask` boundary is still an ordinary control-flow
  edge. Placement therefore sees real branch, loop, and join structure instead of the state-dispatch
  form whose suspend returns to the scheduler and whose resume is entered by a later invocation. The
  emitted coroutine records `IrFunction.LifetimesPlaced`, and the program-wide pass skips an
  already-placed function rather than re-deriving owners from resume prologues and dispatch chains.
  Live-across-await owners are saved and restored by the existing transform because a placed drop
  counts as a use of its owner;
- task frames have an explicit ownership description and teardown. `StateMachineTransform` publishes
  where it saves each temp and local; lowering turns those offsets and the capture words into
  `CoroutineFrameSlot` descriptors recording owner, type-directed release, empty-list tolerance, and
  a stable reason, retained as `CoroutineRepresentationRecord` values. A frame that owns references
  gets a generated dropper which releases each owned word and clears it, so scheduler completion,
  `ashes_cancel_task` (covering race losers and recursive cancellation of an awaited child), and
  spawned-task reaping may each reach it. Ownership moves rather than being shared: creation clears
  the live-variable region, a suspend hands its saved values to the frame, the matching resume
  clears each word as it restores it, and completion clears the word holding the transferred result;
- ordinary RC placement is scoped by a per-function "may execute inside a coroutine" effect instead
  of a whole-program async flag. An async body seeds the functions written inside it and the
  functions it references, the call census propagates, and an unresolvable call inside such a body
  conservatively includes escaped functions. A function that cannot run inside a coroutine keeps
  ordinary placement even though the program creates tasks elsewhere, and a captured value it owns
  becomes a frame-owned reference released by the dropper;
- byte storage origin is explicit and metadata-driven. `BytesOwnershipProvenance` distinguishes fresh
  owned buffers, owner-borrowed views, program-lifetime mmap views, and conservative unknowns in both
  function-result summaries and lowered-temp facts. Owning aggregate boundaries materialize
  owner-borrowed views, conservative and program-lifetime storage remains non-owning, and TCO layout
  support no longer rejects a type merely because it contains `Bytes`.

These pieces are useful foundations, but the gaps below still retain separate ownership or
representation paths. Their existence must not be mistaken for a completed migration.

## 3. Verified current gaps

| Area | Current implementation | Remaining gap |
|---|---|---|
| Async/task frames | Lifetime placement runs on the linear body before the split; task frames carry slot ownership descriptors and a teardown helper invoked on every terminal path; ordinary functions unreachable from a coroutine use normal placement. | Inside a coroutine body `_inCoroutineBody` still forces region treatment, so a value dead before suspension or live across an await does not yet use ordinary placement. A capture that arrives as a parameter has no static representation and needs the dynamic argument-ownership path before the frame can own it. |
| Observability | `FunctionOwnershipSummary` carries `SourceFunctionOrigin`, structured call-census/move-safety/result-reach causes, and compatibility projections for the existing positive facts. Lowering retains structured fact-consumption records for reuse specialization, rejection, reset-safety, entry-copy, uniqueness, layout, token lifecycle, and fallback decisions, plus runtime-managed call-result placement and immutable TCO placement traces. Production `IrFunction` values carry typed `IrFunctionOrigin` lineage which survives semantic IR rewrites and is ignored by the backend. `IrInst` has `SourceLocation`, colliding summaries are retained internally, and `CompileToImage` optimizes the `IrProgram` immediately before backend compilation. | The compatibility ownership formatter does not yet expose the stable origins or structured causes, ownership/placement debug output is environment-driven and emitted inside semantic passes, remaining representation decisions are still transient or reconstructed from instructions, and the structured facts are not yet exposed through an immutable compilation snapshot paired with the final optimized IR. |

## 4. Remaining implementation order

The order below is dependency-driven. Do not start async narrowing until the ownership and frame
teardown prerequisites are in place. Milestones 1–6, 7.1 and 7.2 are complete, and 7.3's first
bullet has landed. Two implementation tasks remain; the rest of Milestone 7.3 is next.

### Milestone 7 — async/task-frame ownership (last semantic cutover)

This milestone must be sequenced as teardown and control-flow correctness first, narrowing second.

#### 7.3 Replace `_usesAsync`/`_inCoroutineBody` allocation gates

The first bullet has landed: ordinary functions proven unreachable from a coroutine now use normal
ownership/placement, and a value such a function owns and captures becomes a frame-owned reference
that the dropper releases. `_usesAsync` remains only for its real control-flow purpose (whether
`await` lowers to `AwaitTask` or `RunTask`).

Remaining:

- inside a coroutine, allow values dead before suspension to use ordinary Perceus placement;
- save live-across-await RC values in the frame with an owned reference and descriptor;
- own a capture whose representation is not statically known, which arrives as a parameter of the
  creating function. The existing runtime argument-ownership flag (`LoadArgumentOwnership`) is the
  mechanism; until then such a capture stays region-backed;
- retain conservative arena behavior for unknown call reachability or unsupported layouts.

Remove `_inCoroutineBody` from ordinary RC eligibility sites only as their replacement facts become
live.

**Falsified: removing the gate wholesale.** Dropping the `_inCoroutineBody` conjunct from the eight
ordinary-value ownership sites (leaving the TCO promotion restriction alone) keeps every existing
gate green — C# 1904, LSP 52, e2e 548, and the HTTP/TLS fixtures repeated in isolation — but leaks.
A coroutine body that binds an owned string and reads it after an await grows from a flat 256 KB to
1280 KB when its loop goes from 2 000 to 20 000 iterations, about 58 bytes per iteration, where the
same program on the current gate stays flat:

```ash
let build n = Ashes.Text.fromInt(n) + "-tail-with-a-longer-payload-so-leaks-show"
let once n =
    async(
        let made = build(n)
        in
            match await Ashes.Task.sleep(0) with
                | Ok(_u) -> "A" + made + "!"
                | Error(_e) -> "err")
```

The existing suites do not catch it: the async fixtures preserve copy scalars across awaits, so a
growing-workload resident-set measurement is the signal. The two remaining bullets above are
therefore per-value obligations, not a gate to delete — a value needs either a proof that it dies
before the first suspend, or a frame slot that owns it with a descriptor. Re-attempting the removal
without one of those will reproduce this leak.

**What leaks, precisely.** The leaked values are the coroutine's *freshly built* result: the
`CopyOutArena` with `RuntimeManaged: true` and `Purpose: RcNormalization` emitted where the body's
escaping expression becomes the task result. No `RcDrop` covers it anywhere. Varying one thing at a
time isolates it:

| coroutine result | resident set at 2 000 / 10 000 / 50 000 iterations |
|---|---|
| a freshly built string | 256 KB / 256 KB / 1536 KB |
| `Ashes.Text.byteLength` of that same string | flat |
| an already-owned binding the body created | flat |

The frame slots themselves are correct — a saved temp or local classifies as `SavedInTaskFrame` and
its body drop is placed. What has no owner is the value handed out through the task's result slot,
which frame teardown deliberately does not touch because completion transfers it to the awaiter.
Nothing on the awaiting side then takes ownership of a reference-counted result. Under region
placement the region reclaimed it, so the missing contract was invisible.

**Prerequisite for both remaining bullets.** Define the task-result ownership contract before
touching the gates again: either the awaiting side takes ownership when it consumes an `AwaitTask` or
`RunTask` result, or the result stays frame-owned until the awaiter has copied it out. Any
in-coroutine reference-counted value can end up as the result, so neither remaining bullet is
implementable without it.

**The task-result boundary, diagnosed.** An `async` block containing no `await` leaks about 60 bytes
per task — 256 KB at 2 000 iterations against 6144 KB at 100 000 — whether or not the awaiter consumes
the result. The body must allocate for it to happen: `async(n + 1)` and `async("literal")` are flat,
`async(build(n))` and `async("A" + build(n) + "!")` leak.

Reading the emitted IR explains the asymmetry with the `await` form, which is flat:

- a block that completes without suspending is lowered inline, *outside* the coroutine-body context,
  so its call-boundary result takes the ordinary runtime-RC normalization path — one
  `CopyOutArena` with `RuntimeManaged: true` and `Purpose: RcNormalization` per task. That allocation
  is stored into the task's result and handed to the awaiter, and no `RcDrop` exists anywhere in the
  program. The enclosing loop's region reset reclaims the task struct and the `Ok(…)` wrapper, but
  not the reference-counted payload;
- with an `await` the same body is lowered under `_inCoroutineBody`, so its result stays
  region-backed — the executed arm concatenates with `RuntimeManaged: false` and the coroutine's
  result leaves through an `ArenaScopeBoundary` copy-out. The region reset reclaims it. The one
  RC-normalizing copy-out in that program sits on the never-taken `Error` arm.

So this is not a separate defect: it is the missing task-result ownership contract, reached through
the no-suspend path instead of through lifted gates. Two ways to settle it:

- **the awaiting side takes ownership.** A runtime-managed value delivered through `ResultSlot`
  becomes an owned value in the consumer's scope when `RunTask` or `AwaitTask` yields it, and the
  existing lifetime placement drops it. Whether a given task's result is runtime-managed has to be
  readable at the consumption site — tasks are first class, so a header word set at creation
  (alongside `FrameDropper`) generalizes to `Ashes.Task.all`, `race`, and spawned tasks, where a
  creation-site-only fact does not;
- **suppress the normalization on the task-result path**, so a value destined for a task result stays
  region-backed exactly as the `await` form already does.

The second is the smaller change and fixes the leak, but leaves 7.3 with nothing: it keeps async
values on regions by construction. The first is the contract both remaining 7.3 bullets need, because
any in-coroutine reference-counted value can end up as the result.

**Pattern-owner list drops are shallow.** `EmitOwnedValueDrop` returns early for a
`PerceusPatternOwner` and emits a bare single-cell `RcDrop`, deliberately bypassing the type-directed
spine walk so precise placement has one anchor to move rather than a tree of child drops. A list
pattern owner therefore releases only its head cell, orphaning the tail whenever its count reaches
zero — reachable when the parent's structural drop runs first, sees the head shared, and stops. A
movable structural drop is the missing piece; until then this is a known leak in the pattern-binding
path rather than an accepted trade.

**Fixed at the producer.** A non-suspending `async` body is now lowered as the coroutine body it is,
so its result stays region-backed exactly as the suspending form's does and the enclosing region
reset reclaims it. The remaining `let`-bound shape came from `LowerEscapingResult`, where only the
closure case consulted the placement context and every other representation decision — string, ADT,
list, `Bytes`, `BigInt`, tuple, record — fired on expression shape alone. They now share one gate, so
an escaping result inside a coroutine body stays region-backed like the body around it. Every
measured shape is flat.

**Gating each producer is the wrong shape.** Lifting `_inCoroutineBody` from the ordinary placement
sites while keeping it wherever a value crosses the task boundary makes every non-suspending shape
flat and leaves the frame machinery working exactly as designed — a saved temp and local classify as
`SavedInTaskFrame`, the dropper is generated, and the body's own drops are placed. The suspending
shape still leaks, and closing it one site at a time found four separate places that decide the
representation of a value on its way out of a coroutine: `LowerEscapingResult`, the call-boundary
normalization in `LowerCallRestoreArena`, the applied-closure call result, and the match-arm result
in `LowerMatchArmExpression`. Each gate closed one and revealed the next.

**A race and a spawn in one program crash, and it predates this work.** A program that resolves an
`Ashes.Task.race` and then drives a task which spawns another segfaults when the racing coroutines
hold any owned heap value — a string, a list or an ADT is enough; the same program with scalar-only
bodies is fine, and either construct alone is fine. The scheduler calls a task's `CoroutineFn`
through offset 8 of a task pointer and finds it null, so a task whose function pointer is zero
reaches the coroutine-call path. It reproduces at `a955eea`, before the task-frame work, so it is an
older defect in the race/spawn scheduler interaction rather than an ownership one. The acceptance
coverage keeps race and spawn in separate programs until it is fixed.

**Two findings from starting the acceptance matrix, both blocking it.** Normalizing the result in one
place and then lifting the in-coroutine gates makes every leak shape flat, both suites green, nine
async fixtures clean under Memcheck, and seven of the thirty-two coroutines in the corpus carry
reference-counted operations against none before — but a **closure capturing an owned value across a
suspend segfaults**, reading its captured value as null after the resume. The same program is correct
without the lift. Keeping in-coroutine closures region-backed does not help, so the fault is in the
captured value's own representation rather than the closure's: the value is reference counted, the
closure environment holds a raw pointer to it, and something releases or clears it across the
suspend. The narrowing waits on that diagnosis.

Separately, one program holding **six async tasks of different value kinds** — string, list, ADT,
tuple, `Bytes`, closure, each built before an await and read after it — exhausts the heap on the
unoptimized pipeline the test runner uses, while the same program through the optimizing path runs
correctly. It reproduces on `main`, so it predates the coroutine work, and it blocks writing the
acceptance matrix because every entry has that shape.

That count is the finding. The decision belongs in one place: the point where the body's value
becomes the task result. Normalize there — to a region representation, or to a frame-owned reference
with a descriptor — and leave every decision inside the body free. Gating producers individually
cannot converge, because any producer can be the one whose value happens to reach the boundary.

**Attempting the awaiting-side contract found the ordering is the reverse of that.** Two obstacles,
both measured:

- a `RunTask` or `AwaitTask` result carries no lowered-temp ownership fact. The registration contract
  covers instructions that *produce* an ordinary value; these deliver one produced elsewhere, so
  "is this result counted?" cannot be answered at the consumption site from existing facts. Making
  the result wrapper reference-counted when its payload is — confirmed in the emitted IR — does not
  help on its own, because the consumer still has nothing to consult;
- a runtime-flag-guarded release cannot serve as the drop anchor. `PerceusLifetimePlacement` removes
  a single `RcDrop` together with the `LoadLocal` immediately before it and re-emits that one
  instruction elsewhere, rewritten to the owner's definition temp. A guarded tree, or a drop that
  first extracts a payload field, does not survive the move. So the consumer's release cannot depend
  on a per-task header flag while still being placed at the value's last use.

Together these say the task result wants to be *uniformly* reference-counted rather than counted-or-
region depending on the task, and uniformity at the result requires the payload to be uniformly
counted too — that is, in-coroutine values must already use reference counting. The result contract
is therefore not a prerequisite for narrowing in-coroutine placement; the two are one change, and the
narrowing has to come first so the result boundary becomes a static fact instead of a dynamic one.
The task-frame teardown that narrowing needs is already in place.

This reverses the order suggested above. It would be falsified by a way to place a representation-
guarded release at a value's last use, which would let the two coexist during a transition.

**Measuring this class of leak.** The shared plateau harness runs its program through a Python
wrapper whose `subprocess.run` forks before exec, so the child's `ru_maxrss` inherits the
interpreter's image: the floor is about 13.5 MB with several hundred KB of run-to-run noise, which
cannot resolve a leak of this size, and the 8 MB default growth budget reflects that floor.
`Linux_backend_llvm_async_coroutine_value_memory_should_plateau` therefore measures through
`/usr/bin/time` — a floor of about 256 KB — and optimizes the IR first so it sees what actually
ships. It fails deterministically with the gate lifted and passes with it in place. Lowering the
floor for the other plateau workloads would make them meaningfully more sensitive too.

#### Milestone 7 acceptance

Existing async fixtures mostly preserve copy scalars across awaits and are insufficient. Add native
soak tests for strings, lists, ADTs, tuples, owned `Bytes`, and closures:

- captured/read on both sides of an await;
- captured across multiple awaits;
- returned after the final await;
- abandoned in a never-completing task;
- cancelled as a race loser;
- held by a spawned task and released on reap.

Release blockers are any invalid read/write, double free, stale arena pointer, or unbounded RC leak.
Use Valgrind or an equivalent native memory checker plus growing-workload RSS measurements.
Covered so far by `tests/async_owned_values_across_awaits.ash`,
`tests/async_owned_values_task_lifecycle.ash` and `tests/async_owned_values_spawn_reap.ash`: every
supported value shape read on both sides of one suspension point and across two, returned as the
task result, cancelled as a race loser, abandoned unrun, and held by a spawned task until it is
reaped. All three run clean under Memcheck, whose leak accounting is blind here because the runtime
allocates through `mmap` rather than `malloc` — growing-workload resident-set measurement remains the
leak signal, and `Linux_backend_llvm_async_coroutine_value_memory_should_plateau` is its gate.

Still to add: a value abandoned in a task that never completes, and the same matrix over owned
`Bytes` and closures on the cancellation and reap paths rather than only across suspension.

### Milestone 8 — establish the structured compiler-decision handoff

This is the handoff to the separate `--explain` implementation, not an instruction to implement the
CLI early.

Expose a read-only compilation result/snapshot from semantic lowering containing:

- the stable `FunctionOwnershipSummary` set, keyed/indexed by reportable function origin rather than
  exposed mutable dictionaries;
- final placement/representation decisions for ordinary values, including region/arena, runtime RC,
  stack where actually supported, task-frame/worker transfer, borrowed view, static lifetime, and
  conservative unknown categories that exist after Milestones 3–7;
- reuse decisions captured at the actual specialization, uniqueness, copy-elision, layout, token, and
  fallback decision sites;
- stable function/value origins and source locations shared by those records and the resulting IR.

Do not duplicate ownership analysis or scan emitted IR to reconstruct reuse/representation reasons.
The later RC report should separately visit the final optimized `IrProgram`, because operation counts
must describe the semantic IR actually handed to LLVM. Compilation orchestration will correlate that
IR with this snapshot using the stable origins.

The snapshot may use names appropriate to the post-refactor architecture; the contract matters more
than the suggested type name. It must:

- be immutable from consumers’ perspective;
- have deterministic iteration/order semantics or provide explicit ordinal sorting keys;
- contain stable enum reason codes with formatting kept outside semantics;
- be retrievable without setting environment variables;
- be observational only: collecting/reading it must not change lowering, optimization, or generated
  code;
- avoid retaining mutable compiler state or full AST subgraphs after compilation.

Keep `ASHES_EXPLAIN_OWNERSHIP`, `FormatOwnershipSummaries`, and the existing shadow output only as
temporary validation compatibility if still useful. Do not route the new snapshot back through those
strings. The follow-on task owns the public `--explain` request model, CLI command support, formatter,
filtering, stderr behavior, and removal/deprecation of the environment variable.

#### Milestone 8 acceptance

- Unit tests retrieve ownership, reuse, and representation records without console interception or an
  environment variable.
- Every record maps deterministically to a source/reportable function origin and, when site-specific,
  a `SourceLocation`.
- Generated reuse, recursive, closure, drop/release, and coroutine functions retain their source
  lineage, or an explicit compiler/type origin where no single source function exists, through
  `IrOptimizer.Optimize`.
- Snapshot collection enabled/disabled (if collection is optional) produces structurally identical
  final IR and byte-identical output under the existing deterministic-build assumptions.
- No semantic pass added by this migration formats user-facing report prose or writes it directly to
  stdout/stderr.

## 5. Final consolidation and deletion gate

The migration is complete only when all of the following are true:

- `FunctionOwnershipSummary` covers every function binding, including colliding local names and mutual
  recursion groups.
- No live ownership decision resolves a function by a flat bare-name table when lexical binding
  identity is available.
- `TcoParamFacts` feeds live decisions; the superseded `Collect*` ownership classifiers and their
  shadow logger are gone.
- `IsRuntimeManagedResultTemp` is a constant-time forward fact with no emitted-instruction scan.
- Pattern-derived references use ordinary owner/alias dup/drop placement, not a TCO-specific
  source-name escape classifier.
- Owned `Bytes` and borrowed views have explicit internal provenance.
- Capabilities and async constrain only code/values that may cross their real dynamic or suspend
  boundaries.
- Task frames deterministically drop every ordinary RC-owned payload on completion and cancellation.
- Arena/copy-out decisions are justified by explicit confinement/placement facts. Arena remains a
  useful representation optimization; it is no longer a fallback ownership model.
- Ownership, reuse, placement, and representation decisions are exposed through the immutable
  Milestone 8 handoff, with stable function lineage, reason codes, and source locations.
- The final optimized `IrProgram` can be correlated with that handoff immediately before
  `IBackend.Compile`; LLVM IR is not needed to recover semantic decisions.
- Existing temporary ownership debug hooks are the only remaining
  `ASHES_EXPLAIN_OWNERSHIP`/direct-console path; this migration has not introduced another one.
- Every deleted legacy classifier has no remaining reader, test, or diagnostic dependency.

Do not delete `CanArenaReset`, top-cell freshness, layout/drop queries, or TCO reset-cost logic merely
because ownership is unified. They answer legitimate downstream representation questions. The cleanup
target is duplicate ownership derivation and ambient state, not every specialized optimization.

## 6. Validation protocol

Use focused TUnit runs while developing:

```bash
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/UniquenessSummaryTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/OwnershipTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/OwnershipProvenanceTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/PerceusLifetimePlacementTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/CoroutineLifetimePlacementTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/CoroutineFrameOwnershipTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/NestedTcoPatternAliasTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/TcoPromotionCostSignalTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/TcoRcEligibilityPredicateTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/StateMachineTransformTests/**"
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/CapabilityRcEligibilityTests/**"
```

At each milestone boundary:

```bash
dotnet build Ashes.slnx
dotnet run --project src/Ashes.Tests -- --no-progress
dotnet run --project src/Ashes.Lsp.Tests -- --no-progress
dotnet run --project src/Ashes.Cli -- test tests
dotnet format Ashes.slnx --verify-no-changes
```

For any change that can affect placement, reuse, TCO, or task lifetime:

1. build a baseline compiler from the milestone’s starting commit;
2. compare `ASHES_EXPLAIN_OWNERSHIP=all` per function;
3. compare emitted IR and whole binaries (`cmp -s`) at `-O0` and `-O2`;
4. run the full `challenges/` set;
5. measure at least `fannkuch-redux` N=8–11, `binary-trees` N=21, `1brc` at 10M/100M rows, and
   `reverse-complement` on the established input;
6. verify checksums/stdout as well as peak RSS;
7. run growing-key/growing-workload memory checks and a native invalid-access/leak checker.

For changes to the Milestone 8 handoff:

1. compare final optimized `IrProgram` values with decision collection enabled and disabled;
2. compare emitted binaries at `-O0` and `-O2`;
3. randomize or reverse internal dictionary insertion in a test fixture and require identical sorted
   snapshot output;
4. assert that every reported generated function resolves to a source or explicit compiler/type
   origin without parsing its LLVM label;
5. assert that source locations survive optimizer and state-machine rewrites where an originating
   instruction exists.

A missed optimization may conservatively fall back to arena. A false ownership claim, UAF,
double-drop, stale pointer, or unbounded leak is a release blocker.
