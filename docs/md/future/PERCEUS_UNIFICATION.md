# Perceus Ownership Unification — Remaining Implementation Plan

Status: in progress.

Audited against `main` at `7ccbef6` on 2026-07-31, together with the specialization-rejection and
constructor-layout decision capture in this change. This document is intentionally a remaining-work
backlog.
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
- `PerceusLifetimePlacement`, which moves ordinary-value lifetime markers to CFG-aware last uses while
  leaving resource cleanup alone;
- `FunctionOwnershipSummary` with parameter borrow/consume facts, uniqueness, result reach,
  `ExpressionFreshness`, `FunctionResultProvenance`, and positional `TcoParamFacts`;
- builtin fresh-result metadata (`BuiltinRegistry.BuiltinModuleMember.ProducesFreshRcResult`) for
  owned `Str`, `Bytes`, and `BigInt` producers, including use by interprocedural result provenance;
- shared top-cell freshness traversal for ADTs, tuples, and lists, including mixed-arm
  reconciliation;
- closure/function-result provenance, call-result freshness, and match-scrutinee ownership fixes;
- capability gating narrowed from “any capability declaration” to
  `_programHasDynamicCapabilityDispatch`;
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

These pieces are useful foundations, but several remain shadow-only or are still fed by the old
classifiers. Their existence must not be mistaken for a completed cutover.

## 3. Verified current gaps

| Area | Current implementation | Remaining gap |
|---|---|---|
| Pattern-derived aliases | `_pendingNestedTcoPatternAliasSites` is slot-keyed, but `EscapingDirectPatternBindings` is a source-name set derived by a TCO-specific AST walk. | Escape/dup placement remains a special classifier D with string-identity and timing hazards instead of ordinary Perceus alias ownership. |
| Lowered temp ownership | `_runtimeManagedResultTemps` records many results eagerly. | `IsRuntimeManagedResultTemp` still falls back to a linear `_inst.Any(...)` scan over a long enumeration of RC-producing IR instructions. Propagation through borrows, joins, calls, and transforms is manual. |
| Bytes | Fresh owned builtin results are metadata-driven. | Borrowed `Bytes` views are represented only as ordinary `TBytes`; `subView`/`mmap` are inferred from producer shape, closure safety is still partly hardcoded, and TCO conservatively rejects every type containing `Bytes`. |
| Capabilities | Static-`provide`-only programs no longer disable ordinary RC. | One `handle` anywhere still sets a whole-program gate, including functions/values that cannot execute under that handler’s dynamic extent. |
| Async/task frames | `StateMachineTransform` computes live temps/locals across each `AwaitTask`. | `_usesAsync`/`_inCoroutineBody` still force broad arena treatment; Perceus placement runs after the coroutine has been split; task frames carry no RC slot/drop metadata and cancellation has no ordinary-value frame teardown. |
| Observability | `FunctionOwnershipSummary` carries `SourceFunctionOrigin`, structured call-census/move-safety/result-reach causes, and compatibility projections for the existing positive facts. Lowering retains structured fact-consumption records for reuse entry-copy elision and runtime-managed call-result placement, plus immutable TCO placement traces. Production `IrFunction` values carry typed `IrFunctionOrigin` lineage which survives semantic IR rewrites and is ignored by the backend. `IrInst` has `SourceLocation`, colliding summaries are retained internally, and `CompileToImage` optimizes the `IrProgram` immediately before backend compilation. | The compatibility ownership formatter does not yet expose the stable origins or structured causes, ownership/placement debug output is environment-driven and emitted inside semantic passes, and most non-TCO reuse/representation decisions are still transient booleans or reconstructed instruction counts. The structured facts are not yet exposed through an immutable compilation snapshot paired with the final optimized IR. |

## 4. Remaining implementation order

The order below is dependency-driven. Do not start async narrowing until the ownership and frame
teardown prerequisites are in place. Milestone 1 and tasks 2.1–2.4 are complete. Thirteen
implementation tasks remain; Milestone 2.5 is next.

### Milestone 2 — separate TCO ownership, placement, and reuse refinements

#### 2.5 Consolidate the remaining reuse refinements

The structural, borrow-inspection, affine-reuse, specialization-generation, candidate-rejection,
reset-safety, entry-copy, reuse-token uniqueness, and constructor-layout cutovers are complete. The
remaining work in this milestone is to retain structured facts for reuse decisions that are still
transient.

When the reuse path produces/consumes a token or leaves a fallback allocation, record that decision
next to the fact that made it. Use actual stable enum values—not speculative prose—for outcomes the
implementation can distinguish. At minimum, the remaining `DropReuse` production and
`AllocReusing` consumption sites must not lose their source function, candidate value/token,
location, and positive or conservative reason.

#### Milestone 2 acceptance

- `fannkuch-redux` N=8–11 retain their checksums/results and bounded baseline footprint.
- `1brc` (10M and 100M rows), `reverse-complement`, and `binary-trees` retain their baseline peak RSS
  and output. These are mandatory because earlier “safe” TCO cleanups regressed them despite passing
  ordinary tests.

### Milestone 3 — make value/temp ownership forward-propagated

This is the maintenance prerequisite for removing classifier C and for making classifier D ordinary
Perceus work.

#### 3.1 Replace `IsRuntimeManagedResultTemp`’s instruction scan

Introduce one forward-propagated lowered-value fact, keyed by temp, that records at least:

- runtime RC, arena/region, borrowed view, or unknown representation;
- owner identity (when any);
- resolved type/layout/drop descriptor;
- whether the value is borrowed, transferred, or newly produced;
- stable value/source origin and the reason for any representation transition.

Populate it at the same central emission/lowering boundaries that currently add to
`_runtimeManagedResultTemps`. Explicitly propagate it through:

- `Borrow` and `RcDup`;
- `if`/`match` joins;
- known and unknown call results;
- constructor/list/tuple/closure construction;
- copy-out and runtime normalization;
- TCO parameter loads/back-edge installation;
- frame save/restore.

Then remove the `_inst.Any(...)` fallback from `IsRuntimeManagedResultTemp`. Once every producer and
forwarding instruction is covered, replace the remaining set lookup with the canonical temp fact and
delete `_runtimeManagedResultTemps`.

Tests must mechanically enumerate every IR instruction with a `RuntimeManaged` result field so adding
a new producer without registering its temp ownership fails loudly. Rewrites in `IrOptimizer` and
`StateMachineTransform` must either preserve the originating location/value identity or explicitly
record a generated origin for newly introduced instructions.

#### 3.2 Make lowering return ownership with the value

Incrementally replace `(int Temp, TypeRef Type)` results with a `LoweredValue`-style record carrying
the canonical temp ownership fact. Do this by category rather than as one repository-wide rewrite:

1. strings, `BigInt`, and owned `Bytes`;
2. ADTs and tuples;
3. lists;
4. closures and call results;
5. TCO and coroutine hand-offs.

Replace mutable request booleans only when their category moves to the explicit context/result:
`_runtimeRc*AllocationRequested` flags currently encode a mixture of “the consumer can own this” and
“emit this allocation in the RC representation.” Those must become separately named request and
result facts, not one renamed ambient flag.

Top-cell freshness remains a valid syntactic construction fact. It should feed the ownership/placement
decision; it should not be deleted or incorrectly substituted with whole-value
`ExpressionFreshness`.

Do not retain `Expr` graphs in the long-lived lowered-value result solely for future explanation.
Snapshot the small source identity/location and the final facts needed by later orchestration; keep
the analysis-only `ExpressionFreshness` map inside the semantic-analysis boundary.

#### 3.3 Centralize ordinary heap layout capability

After temp ownership carries a resolved type, extract the common type/layout question currently
re-derived across `CanRuntimeManageAdt`, `CanRuntimeManageOwnedChildAdt`,
`CanRuntimeManageTcoOwnedChildAdt`, `CanRuntimeManageOwnedTupleType`,
`CanRuntimeManageTcoListElement`, and `CanCopyOutAdt`:

- can this graph be structurally copied;
- can every owned child be structurally dropped;
- which child offsets/drop kinds are required;
- does it contain a resource or borrowed-view exception;
- is runtime reuse supported for this outer cell.

Use one cycle-guarded layout-capability result for those shared questions. Keep expression-specific
construction freshness, top-cell freshness, and TCO profitability as separate callers; they are not
layout facts and should not be folded into the descriptor. Cut over one value category at a time and
delete only predicates proven to be exact duplicates after the extraction.

The descriptor/result must distinguish the concrete rejection categories already present in the
implementation—resource or borrowed-view containment, unsupported child/drop layout, unresolved type,
and unsupported outer-cell reuse—so later representation reporting does not have to rerun these
predicates against a changed type-inference state.

### Milestone 4 — fold pattern-derived aliases into ordinary Perceus placement

Classifier D is the most historically dangerous TCO subsystem and must move in shadow/cutover stages.

#### 4.1 Give pattern bindings stable identity

Stop keying escape facts by source name. Use the pattern binder’s AST identity before lowering and its
local slot afterward. Add adversarial tests where the same binder name appears in different match arms
or nested scopes; those occurrences must not share an escape verdict.

#### 4.2 Compute pattern-binding ownership in shadow mode

Generalize the ownership/reach analysis to answer whether a pattern-extracted reference:

- is borrowed only for inspection or an ordinary call;
- is transferred unchanged to the same TCO parameter;
- is embedded in a new owner and therefore needs a dup;
- escapes independently beyond the parent’s final use.

Shadow-compare this against `CollectEscapingDirectPatternBindings` and the decisions made in
`ResolvePendingNestedTcoPatternAliasSites`. Preserve the existing conservative behavior on any unknown
case.

Store the final alias/escape classification against the stable binder/slot identity and source
location. Dup/drop placement should consume that record, and the same record should later explain
whether the operation came from embedding, independent escape, transfer to the same TCO parameter, or
an unknown conservative fallback.

The required corpus includes all `NestedTcoPatternAliasTests` plus explicit cases for:

- a direct binding passed only to a plain helper call;
- a direct binding embedded in a constructor;
- a direct binding forwarded to the same parameter;
- nested list/tuple/ADT extraction chains;
- copy-typed extracted fields;
- same-named bindings in disjoint arms.

#### 4.3 Cut over dup/drop placement

Make the ordinary ownership/Perceus path place the protective dup and final drop from stable
binding/owner identities. Only then remove:

- `EscapingDirectPatternBindings`;
- `CollectEscapingDirectPatternBindings` and its occurrence-counting helpers;
- `_pendingNestedTcoPatternAliasSites`;
- the corresponding TCO-specific alias activation bookkeeping that has no remaining consumer.

The cutover gate is runtime memory behavior, not disassembly. Ashes binaries have no useful section
headers for `objdump -d`; an empty disassembly diff is not validation.

### Milestone 5 — represent owned buffers and borrowed `Bytes` views explicitly

`TBytes` must remain the user-visible type, but internal ownership provenance must distinguish:

- a fresh owned buffer with a valid RC header;
- a borrowed view into another buffer;
- a program-lifetime mmap view;
- unknown/conservative provenance.

Build this on existing `BuiltinRegistry` metadata rather than another producer-name whitelist:

- mark `Ashes.Byte.subView` as a borrowed view;
- mark `Ashes.IO.File.mmap` as a program-lifetime borrowed view;
- retain explicit owned-result metadata for `append`, `appendByte`, `fromList`, `empty`, `singleton`,
  and fixed-width encoders;
- propagate the provenance through `FunctionResultProvenance` and the Milestone 3 temp fact.

Use the provenance to replace:

- hardcoded qualified-name logic in `IsRuntimeRcClosureCaptureSafeBytesProducer`;
- `IsArenaAllocationFreeBytesOperand` where the forward fact already answers the question;
- the blanket `RuntimeManagedTcoListElementContainsBytes` rejection.

An RC container may directly own an owned buffer. A borrowed view must either retain a proven-live
owner/program-lifetime mapping or be materialized before it outlives that owner. Dropping a view must
never call the RC-header path.

Test view/owner lifetime through closures, ADT/tuple/list fields, function forwarding, TCO, and (after
Milestone 7) task frames.

### Milestone 6 — narrow the dynamic capability exception

`_programHasDynamicCapabilityDispatch` is sound but still whole-program. Replace it with a conservative
per-function “may execute under a live handler post” effect:

- seed functions containing `handle`;
- propagate through the now identity-correct call graph;
- treat unresolved higher-order/dynamic calls conservatively;
- distinguish code that installs or can execute under a handler from unrelated functions in the same
  program.

Feed that effect into explicit placement context rather than checking a global boolean at each
allocation/copy-out site. Keep `BeginLivePostsGuard`/`LivePostsIndex` where a value really can cross a
pending post; this milestone narrows the affected code, not the underlying safety rule.

Acceptance requires:

- existing `CapabilityRcEligibilityTests`;
- a program with a real `handle` plus an unrelated function that still receives ordinary RC placement;
- a helper reachable from the handled dynamic extent that stays guarded;
- higher-order/unknown calls falling back to the safe arena behavior.

### Milestone 7 — async/task-frame ownership (last semantic cutover)

This milestone must be sequenced as teardown and control-flow correctness first, narrowing second.

#### 7.1 Place lifetimes before coroutine splitting

Today `LowerCapturedStringTaskBuildCoroutine` calls `StateMachineTransform.Transform` before the final
program-wide `PerceusLifetimePlacement.Place`, so placement sees a state-dispatch function whose
`Suspend` path returns and whose resumed state is reached through a later invocation, not an ordinary
CFG edge.

Prefer placing lifetimes on the linear pre-transform coroutine IR, immediately before
`StateMachineTransform.Transform`, so its normal CFG contains the `AwaitTask` boundary. Ensure the final
program-wide placement does not process that function a second time. If this ordering proves
incompatible, the alternative is an explicit suspend-to-resume edge model in
`PerceusLifetimePlacement.BuildBlocks`; do not narrow async allocation while neither solution exists.

Add focused placement tests with an RC owner:

- used only before an await;
- live across one await;
- live across multiple awaits;
- live across an await in a coroutine TCO loop;
- live on only one branch after resume.

#### 7.2 Add task-frame ownership descriptors and teardown

`StateMachineTransform` currently returns instructions, state count, frame size, and max temp. Extend
the transform/coroutine metadata with the owned frame slots and their structural drop descriptors.

Generate a per-coroutine frame dropper (or equivalent descriptor-driven hook) and invoke it exactly
once on every terminal path:

- normal completion after the result has been transferred;
- explicit cancellation;
- losing `Ashes.Task.race` tasks;
- spawned-task reaping;
- recursive cancellation of an awaited child;
- any error/early-completion path.

Saved slots must be cleared or have explicit transfer state so resume/completion/cancellation cannot
double-drop them. Captures and live locals/temps need the same accounting. Resource cleanup stays on
its existing path and must not be silently folded into the ordinary RC frame dropper.

Retain representation decisions for values copied across a worker boundary, saved in a task frame, or
kept arena-based because the suspend/call graph is unknown. These are distinct from ordinary
runtime-RC/region placement and must keep their source function/value origin and decisive reason.

#### 7.3 Replace `_usesAsync`/`_inCoroutineBody` allocation gates

Only after 7.1 and 7.2:

- allow ordinary functions proven unreachable from a coroutine to use normal ownership/placement even
  when the program creates tasks elsewhere;
- inside a coroutine, allow values dead before suspension to use ordinary Perceus placement;
- save live-across-await RC values in the frame with an owned reference and descriptor;
- retain conservative arena behavior for unknown call reachability or unsupported layouts.

Remove `_usesAsync`/`_inCoroutineBody` from ordinary RC eligibility sites only as their replacement
facts become live. The flags may remain for their real control-flow purpose (whether `await` lowers to
`AwaitTask` or `RunTask`); that is separate from ownership.

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
