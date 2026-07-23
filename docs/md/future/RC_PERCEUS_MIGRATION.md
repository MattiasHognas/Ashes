# RC Perceus Migration Plan

Status: Phase 8 paper verification in progress. Phases 1-7 and their validation slices are
implemented, but the final paper comparison has reopened the ordinary-value arena fallbacks listed
under Phase 8. The migration is not complete until those blockers are removed or the declared full-RC
target is explicitly reconsidered.

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

Current status: complete as the coverage-expansion phase; Phase 7 owns the remaining escape and
arena-retirement work. The first string slice adds a runtime-managed flag to plain `ConcatStr`
results and a dynamically sized RC allocation path that preserves the existing `{length, bytes}`
payload pointer behind the standard RC header. Lowering enables it only for a local concatenation
immediately consumed by a known non-retaining builtin (`Ashes.Text.length` or `Ashes.IO.print`).
The first Byte slices apply the same dynamic RC layout to local `Ashes.Byte.append`,
`Ashes.Byte.appendByte`, and `Ashes.Byte.fromList` results, plus the fixed-size result of
`Ashes.Byte.singleton` and `Ashes.Byte.empty`, when immediately consumed by `Ashes.Byte.length`.
The fixed-width `Ashes.Byte.u16Le`, `u32Le`, and `u64Le` encoders use the same boundary. Escaping
`Ashes.Byte.subText` results are runtime-managed under the corresponding direct String-consumer
boundary. Fresh `Ashes.Text.fromInt` results now use that boundary as well, while its internal stack
digit buffer remains transient; `Ashes.Text.toHex` uses the same stack-buffer-to-RC-result path.
Fresh `Ashes.Text.asciiUpper` and `asciiLower` copies use the same direct-consumer RC boundary.
`Ashes.Text.fromFloat` and `formatFloat` keep their intermediate fragments as arena scratch but place
their one final result on the direct-consumer RC path in both fixed and scientific notation.
`Ashes.Number.BigInt.fromInt` now places a directly compared local result behind an RC header and
drops it after `Ashes.Number.BigInt.compare`. Directly compared `add`, `sub`, `mul`, `div`, and `mod`
results use dynamically sized RC buffers as well; division and modulo reclaim their unreturned sibling
buffer and runtime scratch immediately. Escaping conversion and arithmetic results remain arena-managed.
`Ashes.Text.fromBigInt` likewise reclaims its decimal-conversion scratch immediately and places its
final String on the direct-consumer RC path; escaping text results remain arena-managed.
Immediately matched `Ashes.Text.parseInt` and `parseFloat` results now place the `Result` container
behind an RC header on both success and error paths. Their success payloads are inline and their error
payloads are interned read-only literals, so dropping the container must not recursively release either child;
escaping parse results remain arena-managed.
Immediately matched `Ashes.Number.BigInt.toInt` results use the same container-only boundary because
their success payload is also inline and their error payload is an interned read-only literal.
`Ashes.Text.parseBigInt` uses a narrower immediate-match boundary: the `Ok(BigInt)` payload must be
consumed directly by `BigInt.compare`. Both the `Result` cell and successful BigInt payload are then
RC-managed; failed-parse output scratch is released before returning the RC-managed error container.
`Ashes.Text.uncons` RC-manages its outer `Maybe` cell only when an immediate match measures both
String views and returns a copy value. The tuple and view descriptors remain scoped arena scratch and
are reclaimed by the enclosing watermark after the RC container is dropped.
The first closure slice RC-manages both closure cells and non-empty environments when every capture
is a copy value and an `if`-selected closure is called immediately. Direct lambdas keep their existing
stack allocation, while escaping closures and closures with runtime-managed or resource-bearing
captures remain arena-managed.
The same closure/environment RC path now moves an eligible runtime-managed String capture into the
closure only when it is an allocation-free-operand concat and the immediate call has a statically
known copy result (`Text.length` or `Text.byteLength`). A synthesized closure dropper releases this
captured owner before the environment and closure cell. Producers with nested arena allocations,
escaping closures, and resource-bearing shapes remain outside this boundary; a negative IR test pins
the nested-producer gate because admitting it strands scratch at a TCO jump.
Scratch-free RC Bytes producers (`empty`, `singleton`, and the fixed-width encoders) use the same
immediately-called closure transfer when the closure returns `Byte.length`. Append and list-conversion
producers remain gated because their nested operands can introduce arena scratch.
Scratch-free `BigInt.fromInt` results likewise transfer into an immediately-called closure whose
copy result is `BigInt.compare`; arithmetic-produced captures remain gated at this boundary.
Escaping string concatenations and migrated Byte/String producer results, affine `ConcatStrTip`
accumulators, literals, views, other builtin-produced strings and Bytes values, and other BigInt
results remain arena-managed. Compile-time evaluation may not fold a runtime-managed concat into an arena literal.
Native correctness and separate 2K/10K/50K RSS-slope tests cover String and Byte allocation,
exact-size free-list reuse, and final `RcDrop` behavior.

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

Task/coroutine decision: task frames remain region-managed rather than ordinary RC values. Their
intrusive ready/waiter links and suspend-resume state are scheduler-owned; detached tasks execute in
private arena chunk chains that are explicitly released on completion, while main-task frames remain
under lexical/TCO arena reset. A repeated 2K/10K/50K `Task.run(async ...)` RSS-slope test guards the
main-task path, complementing the detached-task and async server memory tests. Ordinary RC values
captured across suspension still require explicit sharing rules before that boundary can be enabled.

Cross-thread decision: the current runtime-RC eligibility rules stay strictly non-escaping and do not
publish RC-managed values or closure environments to parallel workers. Structured parallel captures
and results continue to use the existing parent/worker arena plus copy-out boundary, with worker
arenas and per-thread RC free lists reclaimed during join. An IR/native regression test pins this
gate for a shared-list `Task.Parallel.both` workload. Atomic RC or an explicit thread-shared marker
must land before this eligibility boundary is widened; no atomic overhead is added to thread-local RC.

Phase 6 exit audit:

- User ADTs, records, lists, Strings, Bytes, BigInts, closures/environments, and builtin result
  containers each have at least one native-correct, runtime-managed ownership path with focused
  2K/10K/50K RSS-slope coverage.
- Task/coroutine frames and structured-parallel sharing have explicit region/thread decisions and
  regression coverage rather than accidental RC participation.
- Runtime-managed closure capture now covers scratch-free String, Bytes, and BigInt owners with a
  synthesized type-directed capture dropper.
- The remaining arena-backed ordinary-value paths are not accepted as the final design. Escaping
  producers, generic pointer ADTs, polymorphic/function-owned lists, borrowed views, higher-order
  call results, and legacy to-space/blob reuse are the concrete Phase 7 retirement ledger.
- Scoped formatter/parser/BigInt scratch, OS/runtime payload buffers, task regions, and per-worker
  regions may remain arenas only where Phase 7 verifies that they do not represent escaping language
  values and their RSS slopes stay bounded.

### Phase 7: Retire Obsolete Arena/Reuse Paths

Current status: implementation and exit validation complete; Phase 8 owns the final paper comparison
and repository-wide documentation audit. The Phase 6 exit ledger was resolved by carrying
runtime-managed provenance across escaping let results and direct function-result boundaries, then
narrowing copy-out/to-space machinery to the scoped and specialized regions described below.
The first retirement slice carries scratch-free RC String concatenations through a direct nested-let
result. Lowering marks the inner owner moved, propagates runtime provenance through its load, and lets
the receiving scope place the final drop; the old `CopyOutArena` path is absent for this shape.
Scratch-free Bytes singletons, empties, and fixed-width encoders now use the same direct-result
transfer. `Bytes.append` also transfers directly when both inputs are allocation-free values or
`Byte.fromText` views over allocation-free Strings. `Bytes.appendByte` uses the same boundary for an
allocation-free Bytes input and copy-valued byte. Allocating append operands remain on the arena path
until nested producer ownership is carried with them. `Byte.fromList` now
transfers when its input is a fresh copy-element list: the result is RC-managed and the enclosing let
restores its arena watermark to reclaim the temporary list spine. Borrowed/list-variable inputs remain
arena-managed because their ownership is not transferred by this boundary.
Scratch-free `BigInt.fromInt` results also transfer across a direct nested-let result and avoid
`CopyOutArena`. BigInt arithmetic now applies the RC request only to the final result, leaving operand
values and division scratch arena-scoped; direct `add`, `sub`, `mul`, `div`, and `mod` results can
therefore transfer while the enclosing watermark reclaims that scratch. Parse-result escapes remain
gated pending child/provenance support.
Container-only `Text.parseInt`, `Text.parseFloat`, and `BigInt.toInt` results now transfer directly.
Their success payloads are inline and error Strings are interned, so the type-directed escape drop
releases only the RC `Result` cell rather than treating either payload as an owned child.
`Text.parseBigInt` results now transfer as well. The existing tag-aware drop releases an `Ok` BigInt
child only when the Result cell is unique, while the interned `Error` String remains non-owning.
Fresh user-ADT constructors whose fields are all copy values now transfer directly as a single RC
cell. A fully fresh monomorphic recursive ADT tree may transfer as well: nested children are allocated
as RC cells in the same expression and ownership moves into the parent. Monomorphic multi-constructor
ADTs may also own fresh String, Bytes, BigInt, copy-element list, tuple, and record children; their
synthesized tag-aware dropper releases only the live constructor's children. Borrowed recursive or
pointer children remain gated until child ownership is proven. Single-constructor generic ADTs may own
a fresh scalar String, Bytes, or BigInt producer once the constructor application specializes the type
variable. Fully fresh copy-element lists and recursively fresh tuple payloads now specialize through the
same boundary, as do generic copy payloads. Borrowed generic pointer payloads remain arena-managed.
Fully fresh lists whose elements are copy values or fresh owned String, Bytes, BigInt, tuple, list, or
user-ADT producers now transfer through the same direct-let boundary. The type-directed list drop
releases an owned head only from a unique cell and stops after decrementing a shared spine. Literal or
borrowed pointer elements remain arena-managed. Fresh owned `head :: tail` construction uses the same
rule; retaining the runtime-managed tail emits exactly one `dup`, while a consumed tail moves directly.
Fresh monomorphic records with copy fields, including recursively nested fresh record literals, now
transfer directly as well. Fresh String, Bytes, BigInt, copy-element list, and fully fresh tuple fields
are owned and released by the record's type-directed drop. Records that would need to borrow an
existing pointer child remain on the conservative arena path.
Fresh tuples containing only copy values now transfer directly as one RC cell. Pointer-bearing tuple
escape remains arena-managed except for fully fresh nested tuples: each nested tuple is an RC cell,
and a uniqueness-aware recursive drop releases children before their parent. Borrowed tuple children
remain rejected. Fresh owned String producers may also move into a tuple; String literals and borrowed
Strings remain non-owning pointers and therefore keep the tuple on the arena path. Fresh Bytes and
BigInt producers use the same owned-child rule and are released by the parent's recursive drop. A
fully fresh copy-element list may move into a tuple as well. Fresh runtime-manageable user ADTs and
records now join that graph through their type-directed droppers; borrowed aggregate children remain
rejected.
Single-constructor user ADTs may now directly own a fresh String producer. Their parent uniqueness
check guards the String drop; literal and borrowed String fields remain non-owning and keep the ADT
on the arena path. Fresh Bytes, BigInt, and copy-element list fields use the same field descriptor and
recursive drop. Fully fresh tuple fields are admitted recursively as well; borrowed values remain
rejected.
Escaping `Text.uncons` results now materialize an entirely owned RC graph: the outer `Maybe`, success
tuple, and copied head/tail Strings are independent of the source arena. A tag- and uniqueness-aware
drop releases nested children only for the last owner; the immediate-match path retains the same contract.
String concatenation now applies the RC request only to its final allocation. Nested String producers
such as `Text.fromInt` remain arena scratch, then the enclosing scope restores its watermark after the
independent RC concat is formed. This supports both direct result transfer and immediate owned closure
capture without stranding nested allocations.
`Text.fromInt` results now use the same direct transfer boundary; their temporary digit storage is
stack/scoped scratch, while the independently allocated RC String is dropped by the receiving scope.
The scalar `Text.toHex`, `Text.fromFloat`, and `Text.formatFloat` producers now transfer likewise;
formatter fragments remain scoped scratch and are reclaimed after the RC result escapes.
ASCII case copies, `Byte.subText` copies, and `Text.fromBigInt` results also transfer directly. Their
borrowed sources remain below the scope watermark, while fresh BigInt conversion scratch is reclaimed.
An empty-environment top-level function can now return one of these proven runtime-managed values
directly. The caller restores and reclaims the call's arena window without copying the independent RC
result, propagates ownership to the receiving let, and emits the final drop there. Saturated curried
calls follow the statically known returned-closure label chain to the innermost result. Higher-order
values that lose this label provenance and functions returning arena-backed values retain the
conservative copy-out path. Native and RSS-slope coverage exercises this boundary for String, Bytes,
BigInt, copy-element List, copy-field ADT, and fresh record results so the provenance channel is not
coupled to one payload representation.
An exact let alias of a statically known function now retains that label provenance by local-slot
identity, including curried saturation. The exact label also follows that alias into a closure
environment by its capture index. Each closure now carries an immediate-result ownership bit, so an
indirect function parameter or arbitrary closure-producing control-flow result can distinguish an
independent RC result at runtime. When the higher-order result has a concrete shallow heap type, it is
normalized to RC ownership: an arena result is copied into an RC allocation, while an already-owned
result is retained without copying. The normalized result then participates in ordinary scope drops
and may itself cross another higher-order return boundary without losing ownership provenance.
Concrete higher-order list results now use the same runtime ownership channel. Copy-element lists,
`List(Str)`, and nested copy-element lists are rebuilt as complete RC graphs when an indirect callee
returns an arena value; an already-owned result crosses unchanged. Unresolved polymorphic results,
lists with other pointer-bearing element layouts, and opaque closure graphs still use their legacy behavior until
a runtime layout descriptor can construct and drop a complete RC graph. Programs using async/task lowering keep
the established task-region boundary for now; enabling this closure-result channel there caused the
HTTP keep-alive RSS gate to grow linearly and is therefore blocked on suspension-aware ownership.
The runtime ownership channel is consulted only when a saturated result's function-label chain cannot
be resolved statically. A known curried function whose innermost result is arena-managed retains the
existing arena call copy-out instead of being normalized to RC. Promoting that child alone and then
embedding it in an arena-managed ADT would create a mixed-lifetime graph: the child's lexical drop
could free it while the arena parent still points to it. The `tco_deep_adt_accumulator` regression and
a focused direct-curried-list/arena-ADT backend test pin this boundary, while the opaque higher-order
list RSS profile continues to require normalization and plateauing memory.
Fresh values produced directly as a let body now use the same escape request as a directly returned
binding. String, Bytes, BigInt, fully owned List, Tuple, record, user-ADT, scalar parse-result,
BigInt parse-result, and `Text.uncons` graphs therefore survive the scope reset through RC without a
scope-exit `CopyOutArena` or `CopyOutList`. Runtime provenance is preserved when an enclosing scope
temporarily routes the result through a local slot. Requests remain type- and expression-directed:
borrowed pointer children and unsupported producer graphs stay arena-managed.
String matches whose arms combine fresh RC results with interned literals now normalize each literal
once into an RC String. This gives the match one uniform ownership contract, preserves the eventual
drop, and removes repeated copy-out at every enclosing scope. Match-result runtime provenance is now
collected for ordinary matches as well as reuse-token matches, but is propagated only when every arm
is runtime-managed.
Non-async, capability-free function bodies now apply the same direct-result escape rules. Exact known
calls returning fresh supported graphs restore and reclaim their call windows without caller-side
copy-out. A directly returned interned String is normalized once at the callee boundary so its
ownership bit and final drop remain unambiguous. Async/task programs retain the established region
contract and do not enable this function-body promotion; the keep-alive HTTP RSS slope remains the
regression gate for that exclusion.
Fresh escaping closures with no owned pointer captures, or with proven owned String/Bytes/BigInt,
List, tuple, record, or user-ADT
captures and a copy-valued result, now allocate the closure and environment as one RC-owned graph.
This includes closures returned directly from known functions: the caller reclaims the call arena
without shallow `CopyOutClosure`, and a synthesized environment dropper releases each moved capture
when the last closure owner dies. Separate 2K/10K/50K closure-factory loops cover scalar heap,
list, and nested tuple/ADT/list captures and guard those paths against growth.
Closures with arena-backed, resource-bearing, aggregate, or otherwise opaque captures remain outside
this promotion because copying only the environment words would preserve dangling child pointers.
The legacy persistent to-space/blob allocator remains reachable only from the specialized in-place
`Map`/`HashMap` reuse path. It is intentionally retained as a specialized region rather than an
ordinary-value lifetime mechanism: new persistent nodes and copied keys/values live for the retained
map graph, while dead same-key value storage is overwritten in place when its region/capacity checks
prove that safe. Automated 2K/10K/50K peak-RSS slopes now cover repeated `Map` String-value updates
and fixed-key `HashMap` updates, replacing the earlier out-of-band memory claim and guarding both the
fresh and overwrite paths against linear growth.
The shipped `challenges/1brc/brc.ash` source is also compiled and executed by an automated Linux RSS
profile over generated 75K/150K/300K-row inputs. It verifies the queued parallel reducer and persistent
trie allocation path, checks the exact three-station aggregates, and bounds both peak RSS and growth;
the executable pins a four-worker cap so every sample has the same worker topology and the slope
measures retained row work rather than scale-dependent thread/stack startup.
Call lowering now consumes the materialized per-function `FunctionOwnershipSummary` for both reuse
uniqueness and resource-borrow decisions; resource calls no longer re-read the mutable whole-program
function tables or maintain a second borrow cache. The remaining call-site census is confined to
constructing those summaries and proving unique entry to the headerless persistent `Map`/`HashMap`
reuse region. Removing that last proof before the specialized region itself gains RC headers would
restore its defensive whole-tree copy on nested re-entry and reintroduce the measured leak, so it is
an intentional specialized-region input rather than an ordinary-value lifetime fallback.
The Linux memory/performance harness now collects child `ru_maxrss`, user time, and system time through
Python's kernel-backed `resource.getrusage` wrapper. This removes the undeclared GNU `time` dependency
that made every RSS gate fail in the hermetic CI image, retains the narrow transient `ETXTBSY` retry,
and measures even programs that exit too quickly for `/proc` polling. The full 1,622-test compiler
suite passes both on the host and in the CI container with all RSS and CPU gates enabled. The aggregate
`just ci` invocation proceeds through all .NET suites, then currently stops at pre-existing pnpm
advisories in the VS Code extension and docs builder; the remaining formatter, static-analysis,
extension, docs, and target-matrix jobs are therefore run separately for this phase exit.

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

Phase 7 exit evidence (2026-07-23):

- The host compiler suite passes 1,623/1,623 tests, including the kernel-backed RSS/CPU profiles; the
  complete linux-x64 language suite passes 527/527 runnable tests with 44 declarative skips.
- A freshly published win-x64 compiler passes the complete Wine leg: 510 runnable tests, zero
  failures, and 61 declared platform skips. A win-arm64 source-to-link smoke produces a PE with the
  required `IMAGE_FILE_MACHINE_ARM64` (`0xAA64`) machine field.
- The emulated linux-arm64 leg passed 525 runnable cases. Its first run used the immediately preceding
  compiler artifact and reproduced the direct-curried-list UAF; the freshly published compiler then
  passed `tco_deep_adt_accumulator` in isolation, confirming the fix on ARM64. The only remaining
  harness failure is `process_drop_releases_fds`: nested ARM64 `/bin/true` launches exhaust the host's
  QEMU thread capacity and report `qemu_thread_create: Resource temporarily unavailable`. The normal
  process suite and every dedicated resource-drop test pass; this is an emulation-host limitation,
  not an Ashes exit-code or ownership mismatch.
- Formatter stability, the VS Code extension suite, and the documentation-independent portions of
  `just ci` pass. The aggregate dependency gate is blocked only by newly published advisories in
  existing pnpm dependencies. The docs link gate still identifies a stale consolidated ownership-doc
  link, which Phase 8 must repair as part of the required full documentation audit.
- Valgrind and a directly invokable `qemu-aarch64` are not installed on this host. Native memory
  behavior is therefore gated by the automated multi-scale RSS slopes rather than an unavailable
  ad-hoc tool; Wine supplies the feasible Windows runtime check.

### Phase 8: Final Paper Verification And Documentation Audit

This phase starts only after every implementation slice is complete and the resulting runtime has
passed its correctness, memory-safety, memory-behavior, performance, and cross-target validation.

Current status: paper comparison in progress; implementation blockers found. The PLDI'21 paper's
central invariant is stronger than bounded peak RSS: after immediate `dup`/`drop` operations, every
retained heap object must still be reachable, with `dup` delayed and `drop` placed at the earliest
binding/branch death. Its reuse token is the consumed unique cell or null after decrementing a shared
cell, and a constructor must allocate fresh when given null. Ashes implements those rules for values
admitted to runtime RC, including layout-aware recursive drop, fusion/specialization, and
`DropReuse`/`AllocReusing` fresh fallback. The comparison uses the published extended paper:
<https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/>.

The remaining emitter audit classifies the apparent legacy operations as follows:

- `CopyOutArena`/`CopyOutList` with `RuntimeManaged: true` are RC graph-normalization operations, not
  arena lifetime fallbacks. They convert interned or opaque arena results into independently owned RC
  values and are consistent with the target model, although permanent naming should make that clear.
- `AllocAdtToSpace` and `CopyOutArenaToSpace` are confined to the specialized persistent
  `Map`/`HashMap` reuse region. They are an intentional region implementation with bounded RSS gates,
  not the lifetime mechanism for general ordinary values.
- Parent/worker result copying and task-frame arenas remain intentional scheduler/thread boundaries.
  RC values are not published across them until Ashes gains the paper's thread-shared marking or an
  equivalent atomic transition.
- Non-runtime `CopyOutArena`, `CopyOutList`, and `CopyOutClosure` emitted by scope, direct-call, and
  TCO relocation paths are ordinary-value blockers. Unsupported borrowed/mixed pointer graphs can
  also decline reclamation until an outer arena boundary. These paths are safe and measured, but they
  do not satisfy the document's full ordinary-value RC target or the paper's garbage-free invariant.
  In particular, the direct-curried-list regression demonstrates why promoting only one child of an
  arena parent is unsound; the fix must promote the complete owned graph rather than restore a mixed
  representation.

Phase 8 therefore returns to implementation before the documentation rewrite. The next slices must
establish uniform RC ownership for complete escaping aggregate graphs, then replace the non-runtime
scope/call/TCO copy-outs. The final audit will rename or clearly distinguish RC normalization from
arena relocation and will re-run this emitter census before declaring completion.

The first remediation slice now promotes a copy-element list returned by an arena helper before
embedding it in a runtime-managed ADT, so the parent and child form one RC-owned graph. Exact closure
label provenance carries that ownership through direct curried calls. Anonymous runtime-managed match
scrutinees are registered as arm-local owners: ordinary arm cleanup releases them, and tail-call
cleanup releases them before an otherwise-unreachable TCO back-edge. Payload aliases transfer the
owner instead of dropping it underneath an escaping child. The regression exercises 2,000, 10,000,
and 50,000 iterations of `Step(List(Int))` construction and consumption and now has a bounded RSS
slope; the complete 1,623-test compiler suite, including the HTTP keep-alive plateau, remains green.
Task/coroutine matches deliberately remain on their scheduler-owned arena path pending shared-RC
publication. The remaining Phase 8 blockers are the non-runtime scope/call/TCO copy-outs and precise
field-aware release when an RC parent is consumed but one payload is transferred.

The next remediation slice removes ordinary synchronous call-boundary relocation for shallow values
and supported lists: an arena result is now normalized into an RC allocation, its result temp carries
runtime ownership, and lexical/TCO cleanup emits the matching recursive drop. A statically known
arena `List(Int)` identity result plateaus across the same 2,000/10,000/50,000 RSS profile, and the
full compiler suite remains green. Closure results still use their conservative arena treatment, and
calls in async/coroutine or capability-bearing programs deliberately retain scheduler-owned arena
behavior until shared publication is implemented. The remaining emitter blockers are therefore
scope copy-out, TCO relocation, closure graph normalization, and the field-aware parent release above.

Lexical scope exit now follows the same rule for supported synchronous results. When an arena-owned
shallow value or list crosses a reclaiming scope, the copy is allocated with an RC header and its
ownership provenance flows through the function result and caller. A borrowed `List(Int)` scope
result passes the 2,000/10,000/50,000 RSS profile, and the complete compiler suite now passes
1,624/1,624 tests. Async, coroutine, capability, and closure scope results retain their documented
conservative paths. The remaining non-runtime emitter work is TCO relocation and closure graph
normalization, plus precise field transfer and the final emitter census.

Runtime-managed match payload transfer is now field-aware. A unique parent relinquishes the selected
child and frees only its own cell; a shared parent first `dup`s the selected child, then decrements the
parent so the shared graph retains its original ownership. The transferred child remains runtime-owned
through the match result and is released by its eventual lexical or TCO consumer. Both unique/shared
IR paths are covered, the transferred `List(Int)` workload plateaus at 2,000/10,000/50,000 iterations,
and all 1,625 compiler tests pass. This removes the parent-leak workaround from the blocker list;
TCO relocation, closure graph normalization, and the final emitter census remain.

The first TCO-relocation slice moves annotated synchronous String accumulators to RC ownership at
loop entry. Each tail replacement normalizes the new String to an independent RC allocation, drops
the previous accumulator at the back-edge, resets to the fixed loop-entry arena watermark, and clears
the old affine reservation before reclaiming scratch. The replacement drop is explicitly marked as
already placed so the general lifetime pass cannot move it out of the loop; a separate lexical anchor
releases the final accumulator when a copy-valued function result exits, while a runtime-owned result
transfers its ownership to the caller. Large released RC blocks bypass the exact-size free list so a
growing sequence cannot retain one mapping per distinct size. The 2,000/10,000/50,000 growing-String
RSS profile now plateaus and asserts both the back-edge and exit-drop contracts. The same
self-contained scalar replacement path now covers annotated BigInt accumulators, using their
header-derived limb size for RC normalization and the BigInt drop contract. A growing 50,000-bit
accumulator plateaus across the same three scales and contains no non-runtime BigInt relocation.
The first aggregate TCO path now covers annotated copy-element lists that are rebuilt as complete
values on each iteration. Loop entry and replacement normalize the entire spine into RC ownership,
the previous spine is released with the uniqueness-aware recursive list drop, and scratch is reclaimed
to the fixed entry watermark. The 2,000/10,000/50,000 `List(Int)` workload plateaus and contains no
non-runtime list relocation. Cons-growing accumulators that share the previous spine remain separate:
their replacement now allocates one RC cell and moves the previous spine into its tail without a
whole-spine clone, `dup`, or back-edge drop. The final recursive drop releases the complete spine,
and the growing-list 2,000/10,000/50,000 profile plateaus. That profile also exposed page-per-object
fragmentation in the first RC allocator: small RC blocks are now densely bump-allocated in a dedicated
per-thread RC region, released cells retain exact-size free-list reuse, large blocks keep direct OS
allocation/free, and worker cleanup releases RC-region chunks as a unit. The next aggregate TCO
path covers copy-only tuples: their fixed payload is shallow-normalized at entry and
replacement, the previous tuple is dropped at the back-edge, and the 2,000/10,000/50,000 profile
plateaus without non-runtime relocation. Equal-layout copy-field records and user ADTs now follow the
same type-directed fixed-size path; both record and constructor-form IR are covered, and the ADT RSS
profile plateaus across the same scales. Pointer-bearing tuples now normalize their parent and every
supported String, Bytes, BigInt, copy-element list, or nested-tuple child into one RC graph; replacement
uses the layout-aware recursive tuple drop. The owned-child tuple profile plateaus at all three scales
without non-runtime relocation. Single-constructor records now use the same complete-graph rule,
recursively normalizing supported fields before the arena reset and releasing them through the
field-aware ADT dropper; the owned String/List record profile also plateaus at all three scales.
Pointer-bearing multi-constructor ADTs now dispatch on the source tag, allocate each variant's actual
layout size, recursively normalize the selected fields, and use the synthesized tag-aware dropper;
the nullary/owned-child variant profile plateaus. Runtime-managed TCO params now bypass the superseded
static-reuse defensive deep copy, removing its redundant arena String/list staging; the probe contains
no non-runtime relocation. Closure code labels now have separate optional environment-normalizer
metadata alongside each value's destructor. For supported capture layouts the synthesized normalizer
deep-promotes every captured ordinary graph and returns the matching RC dropper for the destination;
resource, nested-function, and unresolved capture layouts keep a null descriptor. Runtime-managed
`CopyOutClosure` is in place; normalizers are shared per lifted code label so the established
32-byte closure layout does not gain per-value metadata or cross an additional arena-chunk boundary.
Fresh closure TCO arguments with copy-valued or already-RC captures now allocate their closure and
environment directly under RC. A per-parameter active flag distinguishes the arbitrary initial
arena closure from RC replacements: the first back-edge does not falsely drop the input, later
back-edges release the prior closure, and function exit releases the final replacement. Arena-backed,
resource-bearing, or nested-function captures decline the reset without emitting shallow
`CopyOutClosure`. The 2,000/10,000/50,000 replacement profile plateaus, and the final emitter census
remains. A TCO back-edge now upgrades newly resolved supported parameter types before it captures its
reset contract; genuinely unresolved types retain the established end-of-inference deferral. This
gives inferred supported accumulators the same RC paths as annotated ones while excluding slots owned
by the specialized reuse regime; inferred cons-growing `List(Int)` and copy-field ADT accumulators now
have no non-runtime relocation. Deferred reset records snapshot result ownership explicitly so later
function frames cannot alias temp IDs. Dead scope, call, and TCO
`CopyOutKind.Closure` branches have been removed, leaving closure copy construction only in explicit
deep-copy/thread-transfer machinery and the RC normalizer. The remaining census is focused on the
legacy non-runtime aggregate/string/list deep-copy paths and their intentional boundary uses.
The first final-census correction extends runtime-owned list graphs beyond inline heads: `List(Str)`
and `List(List(Int))` TCO accumulators now select field-aware RC normalization, allocate fresh cons
replacements directly under RC, and recursively release String or inner-list children. Runtime list
allocations in the affine TCO path now publish their actual ownership provenance at construction,
allowing a normalized String or nested fresh list to become an owned outer-list child without an
optimistic arena/RC misclassification. Admission is frame-coherent: if any live heap parameter is
neither runtime-manageable nor loop-invariant, all parameters retain the conservative arena regime.
This prevents an RC parameter from falling through a declined reset into legacy relocation; the 1BRC
`formatAll`/`reverse` workload is the regression gate. Fully admitted shapes contain no legacy
`CopyOutTcoListCell` or non-runtime list relocation, and their 2,000/10,000/50,000 RSS profiles
plateau. The complete compiler suite passes 1,630/1,630 tests. Lists with more general element graphs
remain in the final census.

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
- Closures are `{code, env, packed env_size/result ownership, dropper}`. The dropper deterministically
  releases moved resources or RC captures; independent optional environment normalizers are shared
  metadata keyed by the lifted closure code label, avoiding per-closure layout growth.
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
