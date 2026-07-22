# RC Perceus Migration Plan

Status: implementation in progress. The ownership-summary, explicit lifetime-IR, erased placement,
and layout-descriptor scaffold tickets are implemented. Runtime reference counting is enabled for
the current narrow local ADT/list slice; broader heap coverage and later Perceus optimization phases
remain pending.

Decision snapshot:

- Target full RC Perceus for ordinary heap values, not a narrow RC island.
- Replace the arena/copy-out model long term. Arenas may remain only for scoped runtime scratch or
  deliberately-specialized regions.
- Heap header and layout changes are acceptable, even if broad, as long as Ashes keeps compiling
  through LLVM to all native target architectures and existing source syntax remains valid.
- Do not introduce stronger source-level ownership constraints for ordinary values. The surface
  language stays pure/immutable and ergonomic; ownership remains compiler-internal.
- Development should be TDD-led with focused tests, targeted memory checks, and leak/use-after-free
  measurement instead of repeatedly running the full slow suite.

This document sketches a migration from Ashes' current arena plus static-reuse memory model toward
an RC Perceus model. It is based on the Perceus technical report, "Perceus: Garbage Free Reference
Counting with Reuse" (MSR-TR-2020-42, v2, 2020-11-29), and the current Ashes compiler/runtime shape
on `origin/main` at `fbef40c`.

## 1. What Perceus Is Trying To Buy Us

Perceus is not "add ordinary reference counting at scope exit." The paper's key goal is precise,
ownership-based reference counting over an explicit-control-flow functional core:

- Insert `dup` and `drop` where ownership actually splits or dies, not merely at lexical scope exits.
- Drop as soon as possible so only reachable data is retained after immediate RC operations.
- Use the precise ownership view to specialize `drop`, fuse `dup`/`drop`, and turn matched dead
  constructors into reuse tokens.
- Use `drop-reuse` plus constructor reuse to make pure functional code run as functional-but-in-place
  (FBIP) on unique data.
- Handle non-linear control flow by lowering it to explicit control flow first, so every path has
  visible cleanup.

The important conclusion for Ashes: runtime RC alone is not the goal. The goal is a compiler-owned
linear resource discipline that makes heap lifetime precise and makes reuse a normal lowering
property instead of a collection of local special cases.

## 2. Current Ashes Baseline

Ashes already overlaps with parts of Perceus, but through arenas and static proofs instead of runtime
reference counts:

- Heap values are allocated in OS-backed bump arenas.
- Non-resource `Drop` instructions are currently no-ops and are elided by `IrOptimizer`.
- Scope and loop reclamation use `SaveArenaState`, `RestoreArenaState`, copy-out instructions, and
  `ReclaimArenaChunks`.
- Recursive ADT accumulators use Perceus-style in-place reuse without runtime RC:
  `AllocReusing`, `AllocAdtToSpace`, value-cell copy/update helpers, and whole-program move analysis
  prove when the defensive deep-copy can be elided.
- Resource values already use an affine discipline with real deterministic drops and diagnostics
  (`ASH006`, `ASH007`, `ASH008`).
- `Lowering.MoveAnalysis.cs` is a whole-program uniqueness/result-reach analysis that exists
  primarily to protect reuse-copy elision.
- The existing `GetUniquenessSummary` test seam already starts turning that analysis into an
  explicit per-function summary, which is the right first slice for RC Perceus.

That means the migration is not from "GC" to "RC"; it is from "arena regions plus special-purpose
reuse proofs" to "precise ownership/RC as the general heap lifetime substrate, with reuse falling out
of the same facts."

## 3. Proposed Direction

Use RC Perceus as an internal representation and lowering discipline for ordinary heap values, while
preserving Ashes' surface rule: users never write move, borrow, dup, drop, or lifetime annotations.

The migration should be staged but not selective in its end state:

1. Keep resource ownership separate and source-compatible.
   Resource diagnostics are already a language guarantee. RC should not weaken use-after-close,
   double-close, or use-after-move checks.
2. Introduce an explicit internal ownership IR before adding runtime behavior.
   We need `Dup`, `Drop`, and `DropReuse`-like operations in a form optimizers and codegen can reason
   about. Existing `Borrow` can stay, but its relationship to owned vs borrowed environments should
   be made explicit.
3. Move from lexical scope drops to precise liveness drops.
   For ordinary heap values, `Drop` must become meaningful and placed at last use or at path-specific
   branch death, not simply emitted at scope exit and later elided.
4. Add RC headers and codegen only after the insertion algorithm is testable.
   The first behavior-changing runtime target should still be narrow for risk control, likely
   ADTs/lists, but this is an implementation slice toward full ordinary-value RC.
5. Re-express existing reuse in Perceus terms.
   `AllocReusing` should become the constructor side of `DropReuse` tokens. Existing reuse tests are
   valuable acceptance tests, but the proof source should shift from bespoke loop analysis toward
   precise ownership plus uniqueness checks.

## 4. Architecture Sketch

### 4.1 Heap Header

RC-managed heap cells need a uniform header. A likely starting point:

- `rc: i64`
- type/layout tag or static drop descriptor where dynamic recursive drop is needed
- payload follows the header

Open design points:

- Whether constructor tags remain payload word 0 or move into the header.
- Whether strings/bytes/bigints share the same header immediately or migrate after ADTs.
- Whether closure environments get a descriptor-based recursive dropper or keep the existing
  closure dropper slot initially.

### 4.2 IR

Add ordinary heap lifetime operations distinct from resource cleanup:

- `Dup(source) -> target` or `Dup(source)` if identity-preserving.
- `Drop(source, type/layout)` with real recursive behavior for RC-managed values.
- `DropReuse(source, type/layout) -> token` for matched constructor cells.
- `AllocWithReuse(target, ctor/layout, token, fields)` as a later replacement for the current
  `AllocReusing` plus field stores, or keep `AllocReusing` as the low-level backend operation.

Avoid making `Drop` carry only a string `TypeName` long term. Perceus needs layout-aware recursive
drop and specialization; string names are too weak for generic ADTs and tuples.

### 4.3 Insertion Pass

Perceus' algorithm uses an owned environment and borrowed environment:

- Owned values must be consumed exactly once.
- Borrowed values are read-only aliases; duplicating a borrowed value inserts `dup` late.
- `drop` is inserted as soon as a binding or branch no longer needs an owned value.
- `match` branches drop owned values and pattern binders that are dead in that branch at branch entry.

Ashes should implement this over the already-desugared, explicit-control-flow core. Capability and
async lowering need special attention: the paper's requirement is that non-linear control flow be
made explicit before RC insertion.

### 4.4 Optimizations

Port the Perceus optimization stack in this order:

1. `dup`/`drop` fusion and trivial `dup` sinking.
2. Drop specialization by constructor/layout.
3. `drop-reuse` token insertion for matches that rebuild same-size constructors.
4. `drop-reuse` specialization with `is_unique` fast paths.
5. Reuse specialization for unchanged constructor fields.

Ashes' current reuse machinery can be used as a guide and regression source, but should not remain a
parallel proof system indefinitely.

## 5. Phased Plan

### Phase 0: Decision Record And Inventory

Deliverables:

- Record the agreed target: full ordinary-value RC Perceus replacing arena/copy-out long term.
- Inventory heap layouts and allocation sites: `Alloc`, `AllocAdt`, `MakeClosure`, string/bytes,
  tuple, list, bigint, task/state, regex/TLS/process runtime cells.
- Classify which allocations are language heap values vs runtime-owned buffers.
- Add design docs for the intended header/layout model, migration gates, and native-target ABI impact.

Validation:

- No compiler behavior changes.
- Existing full build and `.ash` suite should remain green.

### Phase 1: First-Class Ownership Summary

Deliverables:

- Introduce per-function ownership summaries in the type/lowering model:
  parameter consumption, result reach/aliasing, capture ownership, and borrow-only parameters.
- Feed the summaries from today's whole-program analysis first, behavior-preserving.
- Add focused unit tests for summaries on direct calls, higher-order calls, closures, result-fresh
  builders, result-alias builders, and `Map.set`-shape functions.

Why first:

- It matches the existing uniqueness-typing design and creates a stable bridge from current Ashes
  reuse to Perceus' owned/borrowed environments.

### Phase 2: Explicit Lifetime IR, No Runtime RC Yet

Deliverables:

- Split ordinary value lifetime operations from resource cleanup in IR.
- Add a Perceus-style lifetime insertion pass that can emit debug/checked `Dup` and `Drop` markers.
- Keep non-resource drops lowered to no-op initially, but stop relying on lexical scope placement.
- Add an `ASHES_EXPLAIN_OWNERSHIP` trace to inspect inserted operations.

Validation:

- Differential tests prove erasing `Dup`/ordinary `Drop` markers preserves today's program output.
- Snapshot tests over small IR examples verify drop placement at last use and branch entry.

### Phase 3: RC Runtime For One Heap Family

Deliverables:

- Pick one family, preferably user ADTs/lists, and add RC headers plus `dup`/`drop` codegen.
  This is the first behavior slice, not the final scope.
- Keep strings/bytes/bigints/closures on the existing arena path unless needed for ADT fields.
- Implement layout-aware recursive drop for monomorphic ADT/list cells.
- Disable arena reset/copy-out for RC-managed cells in the migrated path.

Validation:

- Existing resource tests still pass and diagnostics stay Ashes-specific.
- New tests show early release where scoped/lifetime arenas previously retained memory.
- Stress tests cover shared persistent data, branch drops, nested ADTs, and recursive lists.

### Phase 4: Drop Specialization And Fusion

Current status: complete. The first optimizer slice fuses adjacent runtime-managed `dup`/`drop`
pairs, including ownership-transfer pairs, while preserving pairs separated by operations such as
`is_unique` that can observe the temporary reference-count change. Statically known recursive ADT
root constructors now use constructor-specialized drops: nullary cells drop directly, while known
recursive constructors skip root tag dispatch and retain uniqueness-guarded child cleanup. A narrow
diamond optimization sinks a pre-branch `dup` into the only branch that consumes it and removes the
other branch's dead drop, but only when that branch cannot observe the source count. Fully fresh
list spines and recursive ADT roots carry a deep-uniqueness fact: unique lists lower to one unchecked
drop loop and unique tree roots omit their root check, while any explicit child/tail sharing clears
the fact and keeps guarded cleanup. Optimizer regressions now name and cover the paper's map-shaped
match diamond as well as Ashes' curried stdlib `List.map` shape. The latter still has erased legacy
lifetime markers because polymorphic function-owned lists have not entered the runtime-RC family;
the test makes that boundary explicit rather than implying partial generic RC support. A native
linux-x64 CPU-time comparison now measures the optimized shared-list RC path against the same IR
converted to arena allocation. That comparison initially exposed a severe blocker: 500K iterations
took about 1.5 seconds of process CPU because every RC cell performed an OS map/unmap. Runtime RC now
uses an exact-size per-thread free list: a last-reference drop links the released block through its
header, and allocation reuses a matching block before requesting more OS memory. Retained blocks are
bounded by the thread's high-water demand for each statically occurring allocation size. Parallel
workers publish and unmap their cached blocks before their TCB is destroyed. Mixed-size reuse,
memory-slope, structured-parallel, and arena-relative CPU regressions pass, so the temporary fixed
performance ceiling has been replaced by the relative Phase 4 gate.

Deliverables:

- Inline constructor-specific drop paths.
- Sink `dup` into branches and fuse `dup`/`drop` pairs.
- Add optimizer tests around the paper's `map` shape and Ashes stdlib equivalents.

Validation:

- RC operation counts fall on unique-list and unique-tree hot paths.
- Performance does not regress badly against the current arena baseline before reuse is enabled.

### Phase 5: Perceus Reuse Tokens

Current status: complete for the heap families currently admitted to runtime RC. Reuse-token creation
is explicit in IR. A dead, statically unique ADT
match scrutinee lowers to `DropReuse`, whose result token is the only value `AllocReusing` may consume;
the token carries the source cell's field count so layout compatibility remains checked. The
arena-backed path lowers `DropReuse` as an identity operation, preserving today's proven-unique
behavior. The runtime-managed path now implements the Perceus contract: `DropReuse` retains a unique
cell as its token, but decrements a shared cell and produces a null token; `AllocReusing` overwrites a
non-null token or allocates a fresh RC cell for null. Native backend tests cover both outcomes.
Lowering now emits runtime-managed tokens for an exhaustive, guard-free match over a live copy-only,
supported self-recursive, nested-record, or narrow pointer-variant RC ADT whose scrutinee is dead. A
same-sized runtime-manageable constructor consumes the token; otherwise the constructor allocates a
fresh RC cell and the arm null-guards and releases its
unconsumed token with constructor-specialized cleanup. Allocation-regime checks prevent a runtime
token from being consumed by a same-sized arena-managed constructor. Before a unique recursive node
is overwritten, constructor-specific lowering releases its dead old child ownership. A recursive
pattern binding used exactly once as a rebuilt field transfers that ownership without a `dup` on the
non-null reuse path; the null/fresh path conditionally `dup`s it because the shared old node retains
its ownership. The same cleanup and transfer rules now cover monomorphic single-constructor records
whose pointer fields are recursively runtime-manageable records. Additional uses conservatively
decline reuse. Monomorphic multi-constructor variants may now join that boundary when every pointer
field is a fresh runtime-manageable record; synthesized tag-dispatch droppers recursively release
heterogeneous record children. Runtime ownership also propagates through an eligible match result, so
a reused value cannot lose its eventual RC drop merely because it is loaded from the match result
slot. Direct parent construction may now move an existing runtime-managed record child into a nested
record or pointer variant; when the original child binding remains live, the parent receives exactly
one `RcDup` instead. Binder-aware receiver analysis covers both ordinary variable uses and
`record.field` reads. A tail-position match is eligible only when every arm consumes its token before
the TCO jump, so cleanup cannot become unreachable. Native RSS slope measurements for recursive reuse
with a transferred subtree, nested-record reuse, record-child pointer variants, and shared existing
record children at 2K, 10K, and 50K iterations plateau within the established budget.
Recursive-accumulator IR tests assert the complete `DropReuse` to `AllocReusing` path, and
representative constant-memory and shared-value reuse programs remain unchanged. Generic,
resource-bearing, mutually recursive, and other unsupported pointer ADTs enter through Phase 6 rather
than weakening the reuse-token invariants. Persistent to-space/blob operations remain confined to the
legacy arena reuse specialization and are explicitly tracked for retirement or narrowing in Phase 7;
the existing `reuse_*` memory-slope regressions continue to guard that path until then.

Deliverables:

- Reframe `AllocReusing` around `DropReuse`.
- Generate reuse tokens from match branches whose scrutinee is dead and whose rebuilt constructor has
  compatible size/layout.
- Preserve current runtime checks for uniqueness where static uniqueness is not known.
- Port existing recursive-ADT accumulator reuse tests to assert the new token path.

Validation:

- Current `reuse_*` tests remain constant-memory.
- Shared persistent values stay correct by taking the non-unique/fresh allocation path.
- Fresh to-space/blob leaks are either eliminated or explicitly tracked as remaining work.

### Phase 6: Broaden RC Coverage

Current status: in progress. The first string slice adds a runtime-managed flag to plain `ConcatStr`
results and a dynamically sized RC allocation path that preserves the existing `{length, bytes}`
payload pointer behind the standard RC header. Lowering enables it only for a local concatenation
immediately consumed by a known non-retaining builtin (`Ashes.Text.length` or `Ashes.IO.print`).
The first Byte slices apply the same dynamic RC layout to local `Ashes.Byte.append`,
`Ashes.Byte.appendByte`, and `Ashes.Byte.fromList` results, plus the fixed-size result of
`Ashes.Byte.singleton`, when immediately consumed by `Ashes.Byte.length`. Escaping string
concatenations and migrated Byte producer results, affine `ConcatStrTip` accumulators, literals,
views, other builtin-produced strings and Bytes values, and BigInts remain arena-managed.
Compile-time evaluation may not fold a runtime-managed concat into an arena literal. Native
correctness and separate 2K/10K/50K RSS-slope tests cover String and Byte allocation, exact-size
free-list reuse, and final `RcDrop` behavior.

Deliverables:

- Migrate strings/bytes/bigints if they are still arena-managed.
- Migrate closure environments and captured heap values.
- Decide how task/coroutine state interacts with RC, especially detached tasks and per-task arenas.
- Add thread-shared marking or a conservative atomic path before exposing RC-managed values to
  parallel worker sharing.

Validation:

- Structured parallelism remains deterministic and memory-bounded.
- Async tests cover values crossing await boundaries.
- Cross-target validation covers linux-x64, linux-arm64, win-x64, and win-arm64 when available.

### Phase 7: Retire Obsolete Arena/Reuse Paths

Deliverables:

- Delete or narrow copy-out instructions that RC makes obsolete.
- Retire whole-program move-analysis fallbacks once per-function summaries and RC uniqueness checks
  cover the same cases.
- Keep arena allocation only for scoped runtime scratch, OS/runtime payload buffers, and specialized
  regions where RC is intentionally not used.

Validation:

- Full `just ci` or equivalent.
- Memory profiles for 1BRC, map/hashmap folds, string/bigint accumulators, parallel workloads, async
  server loops, and resource-heavy tests.

### Phase 8: Final Paper Verification And Documentation Audit

This phase starts only after every implementation slice is complete and the resulting runtime has
passed its correctness, memory-safety, memory-behavior, performance, and cross-target validation.

Deliverables:

- Re-read the RC Perceus paper against the completed implementation and record any intentional
  deviations, missing invariants, or Ashes-specific extensions.
- Resolve every discrepancy found by that comparison before declaring the migration complete.
- After the implementation and paper verification are complete, audit and update all documentation
  under `docs/md/` so specifications, guides, architecture notes, testing instructions, and future
  documents describe the final ownership, RC, reuse, and arena behavior accurately.
- Give RC Perceus thorough permanent coverage, especially in `docs/md/internals/architecture.md`:
  document ownership placement, `dup`/`drop`, uniqueness, reuse tokens and fresh fallbacks, runtime
  layouts and allocator/free-list behavior, remaining arena regions, parallel/thread boundaries, and
  the invariants that prevent leaks, double-free, and use-after-free.
- Update the root `README.md` as part of the same audit, including its memory-management overview and
  documentation links, and add **Koka** under **Inspirations** with a concise attribution such as:
  "Perceus reference counting and reuse analysis for precise, non-tracing memory management."
- Remove or clearly mark superseded migration-era explanations and stale arena assumptions.

Validation:

- Run the full documentation build/check suite and search `docs/md/` for stale terminology and
  obsolete lifetime claims.
- Verify the root `README.md` agrees with the final architecture and links to the authoritative RC
  Perceus documentation.
- Cross-check user-facing documentation examples against the final compiler and runtime behavior.
- Do not mark the RC Perceus migration complete until the repository-wide documentation audit is
  finished.

## 6. Codebase Task Map

The migration has a few clear seams in the current codebase.

### 6.1 Ownership Facts

Current files:

- `src/Ashes.Semantics/Lowering.MoveAnalysis.cs`
- `src/Ashes.Semantics/Lowering.PublicApi.cs`
- `src/Ashes.Tests/UniquenessSummaryTests.cs`

Current state:

- `Lowering.MoveAnalysis.cs` already computes move-safe parameters and result-reach summaries.
- `GetUniquenessSummary` exposes a read view used by `UniquenessSummaryTests`.
- The summary is still tied to reuse-copy elision and private dictionaries, not a general ownership
  contract carried by function/type metadata.

Concrete tasks:

1. Promote the existing `FunctionUniquenessSummary` concept into a named internal ownership summary
   model that can survive beyond reuse analysis.
2. Extend summary tests to cover borrowed-only parameters, closure capture, branch-local drops, and
   functions whose result aliases multiple parameters.
3. Add an explain/debug printer for summaries before changing lowering behavior.
4. Once stable, make reuse-copy elision read the new summary model instead of directly reading the
   move-analysis dictionaries.

Focused tests:

- `dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/UniquenessSummaryTests/**"`
- Add a new `OwnershipSummaryTests` class only if the existing file becomes too reuse-specific.

### 6.2 Lifetime IR

Current files:

- `src/Ashes.Semantics/Ir.cs`
- `src/Ashes.Semantics/StateMachineTransform.cs`
- `src/Ashes.Semantics/IrOptimizer.cs`
- `src/Ashes.Semantics/IrCompileTimeEval.cs`
- `src/Ashes.Tests/OwnershipTests.cs`
- `src/Ashes.Tests/ResourceLifecycleTests.cs`

Current state:

- `IrInst.Drop(int SourceTemp, string TypeName)` mixes ordinary heap lifetime markers, real resource
  cleanup, and closure-resource cleanup.
- Non-resource drops are explicitly elided in `IrOptimizer`.
- State-machine, optimizer, and compile-time-eval temp walkers all know about `Drop`/`Borrow`, so any
  new lifetime IR must be threaded through those walkers.

Concrete tasks:

1. Split resource cleanup from ordinary heap lifetime in IR. A conservative naming direction:
   `DropResource` or `CleanupResource` for file/socket/process/closure-resource cleanup, and
   `RcDrop`/`RcDup` for ordinary heap values.
2. Keep the first `RcDrop`/`RcDup` behavior erased/no-op in codegen so tests can pin placement before
   runtime RC exists.
3. Update all IR source-temp and def-temp walkers in `StateMachineTransform`, `IrOptimizer`, and
   `IrCompileTimeEval`.
4. Rename existing tests that assert "Drop emits for owned strings/lists" so they assert erased
   lifetime markers, while resource tests continue to assert real cleanup.

Focused tests:

- `dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/OwnershipTests/**"`
- `dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/ResourceLifecycleTests/**"`
- `dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/StateMachineTransformTests/**"`

### 6.3 Precise Lifetime Insertion

Current files:

- `src/Ashes.Semantics/Lowering.Ownership.cs`
- `src/Ashes.Semantics/Lowering.Borrow.cs`
- `src/Ashes.Semantics/Lowering.cs`
- `src/Ashes.Semantics/Lowering.Symbols.cs`

Current state:

- `EmitDropsForCurrentScope` emits drops at scope exit from `_ownershipScopes`.
- `PopOwnershipScope` also owns arena reset/copy-out decisions.
- `Lowering.Borrow.cs` handles resource borrow-only classification, but ordinary heap borrowing is not
  yet a Perceus owned/borrowed-environment pass.
- Constructor lowering already has a reuse-token path, but it is tied to TCO/reuse specialization
  state rather than a general `drop-reuse` operation.

Concrete tasks:

1. Introduce a separate lifetime-placement pass over lowered or pre-lowered expression structure,
   with an owned environment and borrowed environment matching Perceus' algorithm.
2. Start with erased markers for variables, lets, calls, constructors, tuples, lists, and matches.
3. Keep resource affine diagnostics on the existing path until ordinary RC placement is proven.
4. Decouple `PopOwnershipScope` into two responsibilities: resource cleanup and arena reclamation.
   This makes later arena removal tractable.
5. Add branch-entry marker tests for `match`, because Perceus' precision depends heavily on dropping
   values dead in one branch before evaluating that branch.

Focused tests:

- Unit tests should inspect IR marker placement directly.
- Add single-file `.ash` tests only when branch/call behavior needs end-to-end lowering.

### 6.4 Heap Layout And Backend RC

Current files:

- `src/Ashes.Backend/Llvm/LlvmCodegenMemory.cs`
- `src/Ashes.Backend/Llvm/LlvmCodegenBuiltins.Http.cs`
- `src/Ashes.Backend/Llvm/LlvmCodegen.cs`
- `src/Ashes.Backend/Llvm/LlvmCodegenExpressions.cs`
- `src/Ashes.Semantics/Lowering.Symbols.cs`

Current state:

- ADTs are payload pointers with tag at offset `0` and fields at `8 + i * 8`.
- Closures are `{code, env, env_size, dropper}` with resource-dropper behavior.
- Strings/bytes/bigints are self-contained buffers without a common RC header.
- Arena save/restore/reclaim is embedded in backend memory helpers.

Concrete tasks:

1. Add an explicit layout description type before changing offsets. This avoids scattering header
   arithmetic across lowering and backend.
2. Migrate one family first, likely user ADTs/lists, by adding an RC header while preserving LLVM IR
   generation and native target support.
3. Introduce backend helpers for `rc_dup`, `rc_drop`, `is_unique`, and `drop_reuse`.
4. Keep existing payload access helpers working through layout functions so old and new heap families
   can coexist during migration.
5. Do not migrate closures/tasks in the first runtime slice; they are entangled with async,
   structured parallelism, and resource droppers.

Focused tests:

- Compile-only backend coverage for linux-x64 first.
- One tiny native run per migrated family.
- Add memory stress only after basic correctness passes.

### 6.5 Arena Retirement

Current files:

- `src/Ashes.Semantics/Lowering.Ownership.cs`
- `src/Ashes.Semantics/Ir.cs`
- `src/Ashes.Backend/Llvm/LlvmCodegenMemory.cs`
- `src/Ashes.Tests/ArenaDeallocationTests.cs`

Current state:

- Many tests intentionally assert `SaveArenaState`/`RestoreArenaState` behavior.
- Arena copy-out is still responsible for many current memory-bounded loops.

Concrete tasks:

1. Keep arena tests during the early RC slices; they guard existing behavior while RC is partial.
2. As a heap family migrates to RC, add tests proving it no longer needs copy-out across scopes.
3. Only delete arena IR when all ordinary heap families that depend on it have moved to RC or to an
   explicitly-specialized scratch region.
4. Split `ArenaDeallocationTests` into legacy-region tests and replacement RC lifetime tests before
   removing old assertions.

Focused tests:

- `dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/ArenaDeallocationTests/**"`
- Target individual `.ash` files under `tests/reuse_*` when changing copy-out or reuse.

## 7. First Concrete Tickets

1. **Summary model hardening, behavior-preserving.** *(implemented)*
   Move the existing uniqueness summary into a stable internal model and broaden
   `UniquenessSummaryTests`. Acceptance: summary tests pass; no IR/codegen changes.

2. **Lifetime IR split, erased behavior.** *(implemented)*
   Add ordinary heap lifetime marker instructions distinct from resource cleanup, update all temp
   walkers, and keep backend behavior unchanged. Acceptance: ownership/resource/state-machine focused
   tests pass.

3. **Perceus placement prototype, erased behavior.** *(implemented)*
   Emit `RcDup`/`RcDrop` markers at last-use and branch-entry positions for a small expression subset.
   Acceptance: marker-placement tests for let/call/match pass; full program output unchanged when
   markers are erased.

4. **Layout descriptor scaffold.** *(implemented)*
   Add a layout abstraction for ADT/list payload offsets without changing the actual offsets yet.
   Acceptance: backend compile-focused tests pass and codegen output behavior is unchanged.

5. **ADTs/lists RC slice.** *(initial narrow implementation and linux-x64 validation complete)*
   Add headers and runtime RC for user ADTs/lists only, with fresh allocation, `dup`, `drop`, recursive
   drop, and basic uniqueness checks. Acceptance: small native runs pass, targeted leak/UAF checks
   pass on linux-x64, and resource diagnostics remain unchanged.

   Current validation includes native peak-RSS slope checks at 2K, 10K, and 50K iterations for
   shared-tail lists and shared-child recursive ADTs. Legacy arena controls currently cover pointer
   lists, transient and growing-accumulator strings, pointer-bearing records, bytes, BigInts, and
   heap-backed captured closures. The growing-string case directly guards the historical
   stranded-copy leak. A live keep-alive HTTP server control now samples RSS after 50, 500, and
   3,000 requests and separately bounds late-phase growth after connection, parser, and response
   initialization have settled. A structured-parallel control repeatedly shares a list across a
   `Task.Parallel.both` worker boundary and bounds peak-RSS growth at the same 2K, 10K, and 50K
   scales. These tests also found and now guard a branch-lowering leak where the first TCO match
   arm's compiler-only release state suppressed RC cleanup in later arms.

## 8. Test And Measurement Strategy

Use focused TDD for every slice:

- Start each implementation slice with one or two small failing tests that isolate the intended
  lifetime behavior or IR fact.
- Prefer direct unit tests for ownership summaries, lifetime insertion, and optimizer rewrites.
- Use single-file `.ash` regressions only when the behavior needs the full frontend/lowering/backend
  path.
- Do not repeatedly run the full suite while iterating; run targeted tests first, then broaden only
  at phase boundaries or before a review handoff.

Memory safety and memory behavior must be measured explicitly:

- Add focused stress programs for each migrated heap family.
- Track RSS/peak allocation for hot loops that previously depended on arenas or reuse.
- Measure memory-growth slopes, not only one-run ceilings: execute representative native workloads
  at increasing iteration counts (for example 2K, 10K, and 50K), subtract process-startup noise, and
  require peak RSS to plateau within a fixed tolerance instead of growing proportionally with work.
- Keep slope regressions for legacy arena-managed families as well as migrated RC families. An old
  arena/copy-out leak is still a release blocker while that path remains executable, and each known
  leak must gain a regression before or alongside its fix.
- Maintain a workload matrix covering unique and shared lists/trees first, then records, strings,
  bytes, bigints, closures, async/server loops, and parallel workers as those families enter scope.
- Use available native tooling per host/target where practical: ASAN-like instrumentation if it
  becomes available, Valgrind or equivalent on linux-x64, Wine-compatible checks for win-x64 where
  feasible, and focused qemu runs for linux-arm64.
- Treat use-after-free, double-free, stale arena pointer, and RC leak regressions as release blockers
  for the slice that introduces them.

## 9. Hard Risks

- **Layout churn.** Existing backend code assumes many payloads are raw pointer-to-payload values.
  Adding headers may touch almost every memory load/store helper.
- **Generic recursive drop.** A string `TypeName` is not enough. We need layout descriptors or
  monomorphized drop functions.
- **Control-flow explicitness.** Perceus relies on cleanup-visible control flow. Ashes capabilities,
  async lowering, `Result` pipes, and task cancellation need an audit before RC insertion.
- **Parallelism.** Perceus avoids atomic RC in the fast path by tracking thread sharing. Ashes already
  has structured parallel workers and per-thread arenas; RC values crossing worker boundaries need a
  clear thread-share transition.
- **Cycles.** Pure immutable ADTs should be acyclic, but closures, tasks, and future mutable cells can
  create reference cycles. The initial migration should state that cycles are unsupported unless a
  value family is proven acyclic or explicitly broken.
- **Performance cliff.** A naive RC pass will likely be slower than the current arena path. Drop
  specialization, fusion, and reuse are not optional polish; they are required for the migration to be
  credible.

## 10. First Implementation Slice

Recommended first slice:

1. Harden the existing uniqueness summary API into an ownership-summary API.
2. Add tests for summary facts without changing IR or codegen.
3. Add a temporary explain flag to print summaries for selected functions.
4. Keep reuse-copy elision reading through this summary model.

This keeps the branch green while making the core Perceus concept concrete in Ashes: function
boundaries carry ownership facts, and later RC insertion composes those facts instead of running a
fragile whole-program census.
