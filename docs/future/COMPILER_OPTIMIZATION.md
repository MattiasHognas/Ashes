# Compiler Optimization — Status & Roadmap

Internal compiler quality-of-implementation improvements.
Nothing here changes the language specification.

---

## Completed Work

All original audit findings have been addressed:

| Area | What was done |
|------|---------------|
| **LLVM passes** | Targeted pipeline (mem2reg, instcombine, early-cse, reassociate, gvn, dce, inline, licm, dse) at O1–O3. PLT32 + PE relocation support. Freestanding builtins (memcpy, memset, strlen, memcmp, bcmp) emitted per module. |
| **Memory allocator** | OS-backed `mmap`/`VirtualAlloc` chunks (4 MB each, on demand). Bounds checking with clean error. |
| **Arena deallocation** | Phase 1: scope watermarks for copy-type results. Phase 2a: TCO per-iteration reset for copy-type args. Phase 2b: copy-out (`CopyOutArena` IR instruction) for `TStr` scope results. Phase 2c: TCO copy-out for `TStr` and `TList(copy-type)` args. Phase 2d: abandoned OS chunk reclamation via `ReclaimArenaChunks` (split from `RestoreArenaState` to prevent use-after-free — restore resets pointers, reclaim frees chunks after copy-out completes). Phase 3: per-function-call watermarks. Phase 4: extended copy-out — `CopyOutList` (deep cons-chain copy for `TList` with copy-type element), `CopyOutClosure` (closure struct + env copy; 24-byte closure layout `{code, env, env_size}`), ADT with copy-type fields. Phase 5: extended TCO copy-out — `CopyOutTcoListCell` for `TList(TStr)` and `TList(TList(copy-type))` args (single-cell + head copy), closure and ADT args via `CopyOutClosure`/`CopyOutArena`. |
| **Extended TCO copy-out** | Replaced `CanCopyOutTcoArg` with `GetTcoCopyOutKind` in `Lowering.cs`. Added `CopyOutTcoListCell` IR instruction for single-cell + head copy-out, and `ListHeadCopyKind` enum (`Inline`, `String`, `InnerList`). `TList(TStr)` via `CopyOutTcoListCell(String)`, `TList(TList(copy-type))` via `CopyOutTcoListCell(InnerList)`, closures via `CopyOutClosure`, ADTs via `CopyOutArena(staticSizeBytes)`. |
| **String operations** | `EmitCopyBytes` → `LLVMBuildMemCpy`. Comparison → `memcmp`/`bcmp`. Literals → `.rodata` global constants (no heap alloc). |
| **Pattern matching** | Tag/zero/non-zero checks → single `CmpIntEq`/`CmpIntNe` + one conditional jump. |
| **Function attributes** | `nounwind` on all functions. `willreturn`, `noalias`, `nonnull`, `readonly`, `memory(read)` on builtins. |
| **CPU targeting** | `--target-cpu` CLI flag; `native` auto-detects via `LLVMGetHostCPUName`/`LLVMGetHostCPUFeatures`. |
| **IR optimizer** | Constant folding (with cross-label propagation), identity/strength reduction, unreachable code elimination, dead code elimination. |
| **Borrow elision** | `ElideBorrowsForConstants` in `IrOptimizer.cs`. Temp aliasing infrastructure: use-def chain tracking per temp (copy-type producers via `LoadConst*` scan, per-temp use count via `CollectUsedTemps`). Copy-type elision: `Borrow` instructions whose source is produced by `LoadConstInt`/`LoadConstFloat`/`LoadConstBool` are removed; all uses of the borrow target remapped to the original source temp. Single-use elision: non-copy `Borrow` instructions whose target is used exactly once are also elided. Transitive chain resolution via `ResolveTemp`. `RemapSourceTemps` helper rewrites all source-temp references in any `IrInst` variant using `with` record syntax. |
| **Drop elision** | `ElideRedundantDrops` in `IrOptimizer.cs` (Pass 4). Removes non-resource-type `Drop` instructions (String, List, Tuple, Function, non-resource ADTs) — these are no-ops in codegen since arena deallocation handles bulk memory reclamation. Resource-type drops (Socket) are always preserved for platform-specific cleanup. Also removes the associated `LoadLocal` when its target temp is only used by the elided Drop, and `StoreLocal` instructions to slots with no remaining `LoadLocal` references — cascading dead code cleanup in a single pass. Uses `BuiltinRegistry.IsResourceTypeName` to distinguish resource types. |
| **TCO** | IR-level tail recursion → loop. `LLVMSetTailCall` on tail-position calls. |
| **Escape analysis** | Conservative stack allocation for proven non-escaping values. Added `AllocStack`, `AllocAdtStack`, and `MakeClosureStack` IR instructions plus LLVM `alloca` codegen. Closures are stack-allocated when used only as direct callees within scope (including captured-env closures), and ADTs are stack-allocated for immediate single-arm constructor destructuring (`match Box(42) with | Box(x) -> ...`) and let-bound values destructured immediately (`let box = Box(42) in match box with | Box(x) -> ...`). |
| **Debug info** | `DW_TAG_auto_variable` for locals, `DW_TAG_formal_parameter` for lambda args. Custom DWARF language code `0x8001`. `isOptimized` wired to `-O` level. |
| **Decision-tree matching** | Matches over >4 single-ADT constructor arms (distinct tags, trivial sub-patterns, no guards) lower to one `SwitchTag` IR instruction → LLVM `switch`. O(n) tag-comparison chain → O(log n) (or O(1) where LLVM picks a jump table). |
| **Jump-table linking** | The image linkers apply switch jump-table relocations (`R_X86_64_64` in `.rela.rodata`, `IMAGE_REL_AMD64_ADDR64` in `.rdata`, defensive `R_AARCH64_ABS64`), so LLVM's O(1) table dispatch links and runs correctly; the `no-jump-tables` attribute was removed. |
| **String-literal interning** | Identical string-literal `.rodata` globals are content-addressed and emitted once per module (`LlvmTargetContext.GetOrAddStringLiteralGlobal`), shared across all functions and internal call sites. Compile-time, bounded, leak-free. |
| **Mutual-recursion TCO** | Eligible `let rec … and …` groups (same arity, identical parameter types, a cross-member tail call) are merged into one self-recursive `dispatch` function with thin per-member wrappers, so the existing single-function TCO turns mutual recursion into a loop. Ineligible groups keep the closure path. **Design constraint:** members can legally have different parameter types (`ping: Int → Str` tail-calls `pong: Str → Str`), which a single shared typed parameter list cannot merge without unifying incompatible types; hence the same-arity + identical-parameter-types gate (verified against each member's inferred type). Heterogeneous-parameter generalization would need distinct per-member slots (an IR-level slot-union loop) or opaque-coercion dispatch. |
| **In-place reuse (Perceus-style, no runtime RC)** | Immutable recursive-ADT accumulators are rebuilt in place instead of reallocated: a one-time defensive deep copy at loop entry makes the accumulator uniquely owned, then matched-and-rebuilt-with-the-same-constructor cells are overwritten (`AllocReusing`). Covers direct accumulators, helper-rebuild inlining, recursive-function specialization, and the full `Ashes.Map.set` shape (multi-param / nested-recursive-returning / helper-rebuilding / intermediate-value linearity). Fresh heap leaf fields (Str/Bytes/tuple keys & values) are materialized into a persistent to-space/blob on insert and overwritten in place on update; a genuinely-new insert node also lands in to-space. Pure readers (result type ≠ the accumulator ADT, e.g. `Map.get : … → Maybe`) are kept off the reuse path so their result cell isn't stranded in the never-reset to-space. A conservative `IsFullyReusing` gate + `AccumulatorIsFullyPersistent` guard the per-iteration arena reset (extended to admit reset-safe accumulators + scalar resource-handle args). Result: string/int/tuple-valued `Map.set` folds are constant-memory. The nested-re-entry leak is addressed by the CO-2 elision below. |
| **Move/linearity reuse-copy elision (CO-2)** | The specialization-path entry deep-copy is now **elided when provably safe**, killing the nested-re-entry leak (an outer loop threading an accumulator into an inner reuse fold no longer re-copies the growing structure per re-entry — `O(re-entries × size)` → constant). A whole-program, on-demand greatest-fixpoint move analysis (`Lowering.MoveAnalysis.cs`) proves the accumulator is uniquely owned at *every* external call site before skipping the copy; the copy stays on any uncertainty, so an incomplete proof can only leak, never corrupt. Predicate: at each call site the accumulator argument is a **move** — either the sole nullary constructor of its type (a seed whose cell reuse can never observably overwrite), or a reference to a move-safe accumulator parameter of the enclosing function that is **move-linear** there (used at most once on any execution path, never captured). Transitive and cycle-guarded; a fold/param is considered only if its name never escapes as a value (so the call-site census is complete). Elision preserves the machinery's exact precondition (a uniquely-owned entry accumulator), so it needs no reuse-internals reasoning. Verified: nested `Ashes.Map.set` re-entry constant at ~5.7 MB across 400–3200 batches (was 8→54 MB), all reuse correctness tests + a retained-accumulator soundness regression green on linux-x64 / win-x64 (wine) / linux-arm64 (qemu). Scope: the specialization (`f$reuse`) path; the direct-reuse prologue copy and non-nullary/higher-order seed shapes stay conservative (copy kept) pending the broader ownership milestone. |
| **Deterministic resource safety** | File/socket/process handles are closed deterministically without GC/RC (Ground Rule 6), via an affine ownership model: recursive `Drop` for resource-bearing aggregates (`Result(_,FileHandle)`, `Some(Socket)`, tuple/list of resources), move-on-destructure and move-on-construction (no double-close), resource drops at the TCO back-edge (fixes the loop-over-files fd leak), `Process` reaping on drop, and deterministic close of resources captured by an escaping closure (a dropper at `closure+24` invoked when the closure is dropped). All runtime gaps closed & verified (fd-bounded under `ulimit -n 64`). |
| **Use-after-close for match-arm-bound resources (CO-4)** | The static use-after-close check (`ASH006`) already tracks resources whether bound by `let` or by a `match` arm, but the `FileHandle` read intrinsics (`Ashes.File.readChunk`, `Ashes.File.readLine`) never consulted it, so a handle destructured from `Ok(fh)` and read after an explicit `Ashes.File.close` compiled silently (it stayed runtime-safe — the read after close returns an `Error`). Wired `CheckUseAfterDrop` into both file-read intrinsics, so a read after close on a match-arm-bound (or `let`-bound) `FileHandle` is now flagged at compile time, matching the existing socket/process behaviour. |
| **Parallel tunables (CO-5)** | The two hard-coded parallelism knobs are now configurable, defaults unchanged. Per-worker stack size: the `--parallel-stack-size <size>` CLI flag (byte count or `K`/`M`/`G` suffix), threaded `BackendCompileOptions.ParallelWorkerStackBytes` → `LlvmTargetContext` → codegen; unset = 1 MiB on linux (`mmap`) and the OS default on win-x64 (`CreateThread`). Grain size for `map`/`reduce`: exposed as an explicit library parameter — `Ashes.Parallel.mapGrained(grain)` / `reduceGrained(grain)`, with `map`/`reduce` = grain 1 (the original split-to-singleton behavior). |
| **Structured parallelism (`Ashes.Parallel.both`)** | Genuinely parallel fork/join of two pure thunks on all three targets, deterministic (result identical to sequential) and memory-bounded, via **per-thread bump arenas** + worker threads + deep-copy-on-join. Per-thread arena mechanism: linux-x64 a GS-segment TCB (`arch_prctl`); win-x64 the TEB `ArbitraryUserPointer` (`gs:0x28`); linux-arm64 real ELF TLS (`thread_local` arena cursors, `TPIDR_EL0`, `PT_TLS` + `R_AARCH64_TLSLE` relocs resolved in the in-house linker; the entry prologue sets `TPIDR_EL0` only when a loader has not — see **CO-3**). Threads: `clone`/`futex` (linux) / `CreateThread`/`WaitForSingleObject` (win); a `lock xadd`/`ldxr-stxr` worker counter caps concurrency and over-budget forks run inline. `both` forks only at a concrete result type (deep-copy-on-join needs it); abstract results run sequential. Worker-stack lifetime on linux is tied to true thread exit via `CLONE_CHILD_CLEARTID`: the kernel zeroes a ctid word and futex-wakes it only after the worker has fully left its stack, and the parent waits on that (non-private `FUTEX_WAIT`) before reclaiming the stack/TCB/arena — distinct from the result-ready word, so the join still consumes the result immediately. (win-x64 already gates reclamation on `WaitForSingleObject`, which waits for full exit.) |
| **arm64 networking + parallelism coexistence (CO-3)** | The arm64 per-thread arena is real ELF TLS (`PT_TLS` + local-exec cursors) and is now enabled for **every** arm64 image, including dynamically linked (networking / extern) ones — so `both` can hand a worker its own arena even in a program that also `dlopen`s rustls. The apparent conflict was never in the TLS *layout*: a dynamically linked image's local-exec `PT_TLS` is reserved by the loader in the static-TLS block (at the same TPREL the in-house linker bakes in), independently of the DTV that backs the dlopen'd module's *dynamic* TLS. The only real hazard was the old entry prologue unconditionally `msr`-ing `TPIDR_EL0` to a private BSS block, which on a dynamic image clobbered the loader's thread pointer (breaking rustls/libc TLS). Fix: the prologue now reads `TPIDR_EL0` and self-initialises it **only when zero** (an unloaded static image); a dynamic image keeps the loader's pointer and resolves its arena cursors through the loader-reserved local-exec slot. Verified under `qemu-aarch64-static -L <sysroot>`: networking-only (HTTPS loopback, extern) still runs; a program linking rustls **and** using `both` runs correctly and memory-bounded (`PT_TLS` + dlopen'd rustls both present); parallelism forks genuinely (`clone`/`futex` observed via `qemu -strace`) in dynamically linked images. **Caveat (separate, pre-existing, target-independent):** because networking is `await`-driven, any `both` reachable from an async state machine currently runs *inline* on all three targets (confirmed on linux-x64), so a single execution does not fork `both` **while** a live TLS session runs — that coupling is orthogonal to the arm64 TLS/arena coexistence solved here (tracked as **CO-7**). |
| **Data-parallel `map`/`reduce` (CO-1)** | `Ashes.Parallel.map`/`reduce` (and the grain-parameterized `mapGrained`/`reduceGrained`) are now genuinely data-parallel via **call-site monomorphization**: above the grain threshold their bodies split the list in half and evaluate the two halves through `both`, and a saturated call at a concrete element type generates a monomorphic self-recursive specialization whose `both` splits see a concrete result and fork (at or below `grain` they run the sequential `plSeqMap`/`plSeqReduce`). Used polymorphically or partially applied they degrade to a correct sequential evaluation (the polymorphic copy, whose `both` sees an abstract result). The specialization references the module's top-level list helpers by-label (static code, empty env) so nothing arena-allocated crosses a fork. Verified deterministic (result identical to sequential, incl. heap-`Str` deep-copy on join) and memory-bounded on all three targets (linux-x64 native, linux-arm64 qemu, win-x64 wine). |

> **Scope decision — runtime string interning rejected.** *Runtime* interning of dynamically-produced
> strings (`concat` / `substring` results) was considered and **rejected**: under the arena memory
> model there is no sound, bounded implementation. A permanent intern region never reclaims, so it
> grows monotonically (a leak that violates the "No GC / deterministic reclamation" invariant);
> interning into the arena instead dangles on the next arena reset (use-after-free). A bounded,
> reclaimed table would require reference counting or GC, both of which the language forbids. We
> therefore intern only the compile-time-known literal set, which is finite, static, and leak-free
> by construction.

---

## Roadmap — remaining optimization work

Open items surfaced by the in-place-reuse, resource-safety, and parallelism work (consolidated here
from the former REUSE_ANALYSIS / RESOURCE_SAFETY / STRUCTURED_PARALLELISM design docs). Referenced by
stable IDs.

| ID | Item | Notes |
|----|------|-------|
| **CO-2** | **Skip the redundant reuse entry deep-copy for a moved accumulator (move/linearity analysis)** | **Implemented (conservative) for the specialization-reuse path.** The redundant entry deep-copy is elided when a whole-program move analysis proves the accumulator is uniquely owned (moved, unaliased, transitively down to a sole-nullary seed) at *every* external call site; the copy stays on any uncertainty, so the analysis can only leak, never corrupt. This removes the nested-re-entry leak (`O(re-entries × size)` → constant) while keeping every reuse correctness test and a retained-accumulator soundness regression green on all three targets. See the detailed write-up below. Remaining under the broader ownership milestone: the direct-reuse prologue copy, non-nullary/higher-order/fresh-construction seeds, and non-`Ashes.Map` shapes (all conservatively keep the copy today). |
| **CO-6** | **Verify win-x64 parallelism + networking coexistence** | Non-networking win-x64 parallelism is verified (wine); the TEB `gs:0x28` arena has not been tested together with rustls-on-Windows TLS. Unlike arm64 (CO-3), the win-x64 TEB slot is genuinely per-thread and not owned by any loaded runtime, so the CO-3 clobber hazard doesn't apply; the open question is only whether rustls-on-Windows perturbs `gs:0x28`. Validation task. |
| **CO-7** | **`both` runs inline inside an async state machine** | Target-independent and pre-existing (observed on linux-x64, linux-arm64, and unaffected by the CO-3 change): any `Ashes.Parallel.both` reachable from an `await`-driven entry/state-machine currently lowers to the inline (sequential) path rather than forking a worker. Because all networking is `await`-driven, this means a single program does not fork `both` **while** a TLS/HTTP session is live — the arm64 arena/TLS layers coexist (CO-3), but the runtime scheduler does not yet overlap a fork with an in-flight async task. Making them overlap needs the async lowering to preserve `both`'s forkable (concrete-result) shape through the state-machine transform, plus a fork that is safe to interleave with the single-threaded async runtime. Design/validation task; no correctness regression (inline is always a correct fallback). |

---

### CO-2 — detailed analysis (nested reuse re-entry leak)

**Status: implemented (conservative) for the specialization-reuse path.** A whole-program
move/linearity analysis now elides the entry deep-copy when it can prove the accumulator is already
uniquely owned at every external call site; the copy stays on any uncertainty, so the elision can
only leak (never corrupt). The analysis and its soundness argument are in the *"Implemented elision"*
section at the end; the material below documents the mechanism, the leak, and the requirements the
elision was built to satisfy.

**Where the entry deep-copy is emitted.** In `Lowering.LowerLambda` (the innermost-TCO branch,
`isInnermostTco`), the compiler records accumulators that will be reused in place and, after the body
is lowered, emits a one-time defensive deep copy of each, splicing it in at `reuseInsertIndex`. That
index is captured *before* the loop body label is emitted, so the copy sits at the **function's
prologue**, not inside the loop. Two tagging paths feed it:

- *Direct reuse* — the loop body itself matches the accumulator and rebuilds it with the same
  constructor (a `field-bearing AllocReusing` fired).
- *Specialization reuse* — the accumulator is passed as the last argument to a specializable
  recursive function `f` (e.g. `Ashes.Map.set`), which is cloned to `f$reuse`; the accumulator is
  deep-copied so `f$reuse` may overwrite its nodes in place.

The copy loads the slot, calls `EmitDeepCopy`, stores it back, then the block is moved up to the
prologue. Because it is in the prologue, a single flat TCO loop pays for it **once**.

**Why nesting leaks.** An inner reuse fold is a *separate function*. When an outer loop threads an
accumulator into it — `outer(...)(setFold(0)(n)(m))` — each outer iteration is a fresh *call* to the
inner fold, so the inner fold's prologue copy runs again, deep-copying the whole (growing)
accumulator. The copy lands in the persistent to-space/blob that in-place reuse never resets, so it
is not reclaimed: total cost `O(outer-iterations × accumulator-size)`.

**Re-measured on current `main` (peak RSS via `wait4`/`ru_maxrss`), nested `Ashes.Map.set` fold,
map of 300 keys, `outer` batches × 300 inner sets:**

| batches | nested (this shape) |
|---|---|
| 400 | 8.0 MB |
| 800 | 14.4 MB |
| 1600 | 27.7 MB |
| 3200 | 54.1 MB |

Peak RSS ≈ doubles when `batches` doubles → confirmed `O(batches × map-size)` (≈ one full map
deep-copied per re-entry, ~16 KB/batch, never reclaimed). A flat fold of the *same total inner work*
(15 000 / 240 000 / 960 000 `set`s into a 300-key map) is **flat at 5.7 MB** — the prologue copy ran
exactly once. Output is **correct** in every case (300 distinct keys) — this is a leak, not a
miscompile. `tests/reuse_nested_reentry_correct.ash` is a small, green regression that guards the
*correctness* of the nested-reuse shape (it does not, and cannot in the harness, assert peak RSS).

**What a sound elision requires.** Elide the prologue copy for accumulator parameter `p` of fold
function `F` only if, at **every** call site of `F`, the argument bound to `p` is *moved*: dead on
all paths after the call **and** not reachable through any other live alias (a `let`-binding used
later, a value captured into a closure/tuple/list, a second argument position, …). If any caller
retains the accumulator, `f$reuse` overwriting its cells in place corrupts a still-live value —
exactly the corruption the roadmap warns about. Proving move + non-aliasing across call sites is
**path-aware interprocedural linearity**, and it is transitive (the caller's `p` is unique only if
*its* own accumulator was unique, up the call chain). This is the ownership/borrowing milestone.

**Why there is no sound *and useful* local shortcut (finding from the re-measurement).** The one
case provable purely intraprocedurally — a fold whose accumulator has exactly **one external call
site** passing a **syntactically fresh** allocation (a nullary/`empty` constructor or a fresh ctor
application, provably unaliased) — is precisely the flat single-call fold, which **already runs the
prologue copy once and is already constant-memory** (5.7 MB above, independent of work). Eliding its
copy saves one `O(size)` copy at *startup* for **zero** steady-state memory benefit. Every
leak-relevant shape is a fold called *inside a loop*, and a loop-called fold's accumulator argument
is by construction the enclosing loop's *threaded* accumulator (e.g. `m` in
`outer(...)(setFold(0)(n)(m))`) — semantically moved (`m` is dead after the call, its slot is
overwritten by the result), but provably so only by the transitive whole-program argument above.
Concretely, `setFold`'s two call sites both pass a move: `outer`'s body passes `m` (dead after), and
`setFold`'s self-recursion passes the fresh `set(...)` result — yet proving `m` is a move needs
`outer`'s own accumulator to be unique, which needs `outer`'s caller (`outer(0)(30)(empty)`, fresh)
to move it, up the chain. **So the leak is genuinely elidable, but only with the interprocedural,
transitive proof — no local increment both is sound and removes the leak.**

Two viable implementation strategies, both deferred:

1. *Definition-directed*: at `F`'s definition, prove all call sites move+own the arg, then drop the
   prologue copy. Needs a whole-program call-site + aliasing pass.
2. *Call-site specialization*: generate a no-copy `F$moved` clone and route only provably
   move+own call sites to it (leaving the safe copy on the default entry). Preferred — it keeps the
   defensive copy on any unproven or newly-added call site, so a proof gap degrades to a (correct)
   leak, never to corruption.

**Concrete minimal pass the milestone must add (design sketch).** A Semantics-phase analysis (no
Backend dependency), computed over the AST *before* `LowerLambda` emits the prologue copy:

1. *Call-site census* — one whole-program walk building, per candidate fold `F` and its accumulator
   parameter `p`, the list of `(enclosingFunction, argExpr)` for every saturated call to `F`.
   Lowering already discovers the candidate set and its reuse-call args
   (`_specializableFunctions`, `CollectSpecializableCallArgs`, `CollectCtorMatchedScrutinees`);
   extend that to record *all* syntactic call sites, not only the ones inside the fold being lowered.
2. *Local move/alias check per call site* — the arg passed to `p` is a **move** iff it is either
   (i) a syntactically fresh allocation (ctor application or literal — unaliased by construction), or
   (ii) a `Var v` that is (a) a `let`/parameter binding, (b) **dead** on every path after this call
   in the enclosing function body (no later occurrence — an intraprocedural last-use/liveness check
   over the AST, buildable from the existing free-variable/occurrence scans), and (c) **not aliased**
   — never captured by a closure, stored into a tuple/list/ADT that outlives the call, returned, or
   passed to a second parameter position that retains it.
3. *Transitive fixpoint* — `p` is move-safe iff every call-site arg is a move by (2), where a
   `Var v` of kind (ii) additionally requires `v`'s own defining parameter (when `v` is itself an
   accumulator parameter of the enclosing function `G`) to be move-safe. Solve as a monotone
   greatest-fixpoint: assume all candidate params move-safe, retract any param with a call-site arg
   that fails the local check or references an already-retracted param, iterate to fixpoint.
   **Conservative default is not-move-safe** (copy stays), so an incomplete analysis never corrupts.
4. *Wiring* — for a fold whose accumulator param is move-safe at every site, suppress the prologue
   `EmitDeepCopy` via strategy 2 (`F$moved` clone routed only from proven sites). Re-measure: the
   dominant leak term is the entry deep-copy, but the reuse path also materialises fresh leaf fields
   (Str/Bytes/tuple keys/values) into a **persistent to-space that in-place reuse never resets** — for
   a repeated-key workload that to-space is bounded to one map, so eliding the entry copy should
   remove the per-batch term; a genuinely-*new*-key-per-batch workload would still need the outer
   back-edge to reset that to-space. This must be confirmed empirically once the elision is enabled.

**Former blocker (now partly built).** `Lowering.Ownership.cs` models only *affine ownership of
resource handles*. The elision below adds the four pieces it lacked — a self-contained value-level
move analysis in `Lowering.MoveAnalysis.cs` — rather than extending the resource model: a
whole-program call-site census, an intraprocedural path-aware move-linearity check, a transitive
greatest-fixpoint, and a conservative seed rule. It covers the specialization path; the direct-reuse
prologue copy and richer seed/aliasing shapes remain for the full ownership milestone.

### CO-2 — implemented elision

`Lowering.MoveAnalysis.cs` runs once over the fully-desugared program (which is a single nested
let-chain holding the stitched stdlib bindings, the user's declarations, and the trailing
expression). `Lowering.LowerLambda`, at the point it would register the specialization-path entry
copy for accumulator `p` of fold `F` (known from `TcoContext.SelfName`), consults
`IsReuseAccumulatorMoveSafe(F, p)` and skips the copy only when it returns true.

**Predicate.** `IsParamMoveSafe(F, p)` (on-demand greatest fixpoint, memoized, cycles → false) holds
iff `F`'s name never escapes as a value (so its call-site census is complete) and, at every external
(non-self-recursive) call site, the argument bound to `p` is a **move**:

- a **sole-nullary-constructor seed** — a value (following top-level value aliases, e.g.
  `Ashes.Map.empty → Empty`) that is the *only* nullary constructor of its type; or
- a **`Var v`** that is a parameter of the enclosing function, is **move-linear** there — used at most
  once on any execution path (`MaxPathOccurrences ≤ 1`: branches take the max, sequential
  sub-expressions sum, a nested-lambda capture or unmodeled node forces decline) and never captured —
  and whose parameter is itself `IsParamMoveSafe` (the transitive, interprocedural step).

Anything else keeps the copy.

**Soundness.** The entry copy's *only* role is to give `f$reuse` a uniquely-owned accumulator to
overwrite; proving the entry is already unique reproduces that exact precondition, so the elision
needs no reasoning about reuse internals. Move-linearity guarantees no other live reference to the
argument exists on the path that moves it. The sole-nullary seed is safe because its cell holds only
its tag: the only reuse token it can yield is a 0-field token, consumed — in a well-typed program —
to rebuild that same unique nullary constructor, writing an identical tag (a no-op), so it can never
be observably mutated even when shared or retained (field-bearing seeds have no such guarantee and
are rejected). Transitivity bottoms out at such seeds; the conservative default (copy stays) makes
every unproven or unmodeled shape a *leak, never a corruption*.

**Verified.** Nested `Ashes.Map.set` re-entry (300-key map, 400/800/1600/3200 batches) drops from
`O(batches)` (8.0 / 14.4 / 27.7 / 54.1 MB) to a flat ~5.7 MB — the copy now runs once, not per
re-entry. All seven `tests/reuse_*.ash` stay green (the three that would corrupt under a naive
unconditional elision — a retained field-bearing accumulator — are correctly *declined*), plus a new
`tests/reuse_move_elision_soundness.ash` that in one program elides a move-safe fold and declines a
sibling fold whose accumulator is retained (`keep` reads 15, not the corrupted 902). Output is
identical to the copy-in-place baseline on linux-x64 (native), win-x64 (wine), and linux-arm64
(qemu). Full gate (build, C# + LSP tests, `test tests`, `dotnet format`) green.

> **Adjacent bug found while building the repro (out of CO-2 scope, not fixed here):** a TCO loop
> whose accumulator is a recursive ADT in a **non-last curried parameter position** miscompiles to a
> SIGSEGV even at one iteration (the last-parameter form of the identical program is correct). This is
> a per-iteration arena-reset / copy-out gap keyed on parameter position, independent of in-place
> reuse (it reproduces with reuse never firing). Tracked separately; the CO-2 repro and regression
> test keep the ADT accumulator in the last position to avoid it.
