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
| **Move/linearity reuse-copy elision (CO-2)** | The reuse entry deep-copy (the specialization `f$reuse` path and the direct-reuse prologue) is elided when a whole-program move analysis (`Lowering.MoveAnalysis.cs`) proves the accumulator is uniquely owned at every external call site; the copy stays on any uncertainty, so it can only leak, never corrupt. An accumulator argument is a **move** when it is a sole-nullary seed, a fully-fresh construction, a move-linear reference to a move-safe accumulator parameter, a `let`-bound fresh value proven dead-after-use, or a **registered-function call admitted by the result-reachability (may-alias) summary**. That summary (`ComputeResultReach`, a monotone least fixpoint) records, per function, which of its own parameters the result may alias (per-parameter multiplicity capped at 2 — internal sharing poisons via hierarchical **path tokens** + per-binding identity tokens) plus a **poison** flag; `f(args)` is a move iff not poisoned and every reached parameter's argument is itself a move (`IsResultAliasMove`). Covers **result-fresh builders** (reach {}, incl. recursive `let rec build`), **`wrap`-style result-alias builders** (reach {x}), **higher-order / closure-produced seeds** (capture-aware over-application reach), and **`Ashes.Map.set`-shape reuse-rewriting results** (nested-rec-return registration; reach {map,key,value}). Remaining conservative (the correct boundary short of full ownership): a result the summary *poisons* — reaches a global or an unmodeled shape. Measured: nested `Map.set` re-entry `O(batches×size)`→constant; a recursive-builder-seeded fold 504 MB→4.7 MB; a `Map.set`-result-seeded fold ≈2× (200k-key). Full soundness argument, extensions, and test list in the *CO-2 reference* section below. |
| **Deterministic resource safety** | File/socket/process handles are closed deterministically without GC/RC (Ground Rule 6), via an affine ownership model: recursive `Drop` for resource-bearing aggregates (`Result(_,FileHandle)`, `Some(Socket)`, tuple/list of resources), move-on-destructure and move-on-construction (no double-close), resource drops at the TCO back-edge (fixes the loop-over-files fd leak), `Process` reaping on drop, and deterministic close of resources captured by an escaping closure (a dropper at `closure+24` invoked when the closure is dropped). All runtime gaps closed & verified (fd-bounded under `ulimit -n 64`). |
| **Use-after-close for match-arm-bound resources (CO-4)** | The static use-after-close check (`ASH006`) already tracks resources whether bound by `let` or by a `match` arm, but the `FileHandle` read intrinsics (`Ashes.File.readChunk`, `Ashes.File.readLine`) never consulted it, so a handle destructured from `Ok(fh)` and read after an explicit `Ashes.File.close` compiled silently (it stayed runtime-safe — the read after close returns an `Error`). Wired `CheckUseAfterDrop` into both file-read intrinsics, so a read after close on a match-arm-bound (or `let`-bound) `FileHandle` is now flagged at compile time, matching the existing socket/process behaviour. |
| **Parallel tunables (CO-5)** | The two hard-coded parallelism knobs are now configurable, defaults unchanged. Per-worker stack size: the `--parallel-stack-size <size>` CLI flag (byte count or `K`/`M`/`G` suffix), threaded `BackendCompileOptions.ParallelWorkerStackBytes` → `LlvmTargetContext` → codegen; unset = 1 MiB on linux (`mmap`) and the OS default on win-x64 (`CreateThread`). Grain size for `map`/`reduce`: exposed as an explicit library parameter — `Ashes.Parallel.mapGrained(grain)` / `reduceGrained(grain)`, with `map`/`reduce` = grain 1 (the original split-to-singleton behavior). |
| **Structured parallelism (`Ashes.Parallel.both`)** | Genuinely parallel fork/join of two pure thunks on all three targets, deterministic (result identical to sequential) and memory-bounded, via **per-thread bump arenas** + worker threads + deep-copy-on-join. Per-thread arena mechanism: linux-x64 a GS-segment TCB (`arch_prctl`); win-x64 the TEB `ArbitraryUserPointer` (`gs:0x28`); linux-arm64 real ELF TLS (`thread_local` arena cursors, `TPIDR_EL0`, `PT_TLS` + `R_AARCH64_TLSLE` relocs resolved in the in-house linker; the entry prologue sets `TPIDR_EL0` only when a loader has not — see **CO-3**). Threads: `clone`/`futex` (linux) / `CreateThread`/`WaitForSingleObject` (win); a `lock xadd`/`ldxr-stxr` worker counter caps concurrency and over-budget forks run inline. `both` forks only at a concrete result type (deep-copy-on-join needs it); abstract results run sequential. Worker-stack lifetime on linux is tied to true thread exit via `CLONE_CHILD_CLEARTID`: the kernel zeroes a ctid word and futex-wakes it only after the worker has fully left its stack, and the parent waits on that (non-private `FUTEX_WAIT`) before reclaiming the stack/TCB/arena — distinct from the result-ready word, so the join still consumes the result immediately. (win-x64 already gates reclamation on `WaitForSingleObject`, which waits for full exit.) |
| **arm64 networking + parallelism coexistence (CO-3)** | The arm64 per-thread arena is real ELF TLS (`PT_TLS` + local-exec cursors) and is now enabled for **every** arm64 image, including dynamically linked (networking / extern) ones — so `both` can hand a worker its own arena even in a program that also `dlopen`s rustls. The apparent conflict was never in the TLS *layout*: a dynamically linked image's local-exec `PT_TLS` is reserved by the loader in the static-TLS block (at the same TPREL the in-house linker bakes in), independently of the DTV that backs the dlopen'd module's *dynamic* TLS. The only real hazard was the old entry prologue unconditionally `msr`-ing `TPIDR_EL0` to a private BSS block, which on a dynamic image clobbered the loader's thread pointer (breaking rustls/libc TLS). Fix: the prologue now reads `TPIDR_EL0` and self-initialises it **only when zero** (an unloaded static image); a dynamic image keeps the loader's pointer and resolves its arena cursors through the loader-reserved local-exec slot. Verified under `qemu-aarch64-static -L <sysroot>`: networking-only (HTTPS loopback, extern) still runs; a program linking rustls **and** using `both` runs correctly and memory-bounded (`PT_TLS` + dlopen'd rustls both present); parallelism forks genuinely (`clone`/`futex` observed via `qemu -strace`) in dynamically linked images. **Caveat (separate, pre-existing, target-independent):** a `both` does **not** temporally overlap an in-flight async I/O — the async runtime is synchronous/blocking, so a fork runs before or after a live TLS session, never concurrently with one. (The earlier wording here — that `both` "runs inline" in an async program — was a misdiagnosis: a concrete-result `both` forks normally regardless of async usage; see **CO-7** and its detailed analysis.) That temporal coupling is orthogonal to the arm64 TLS/arena coexistence solved here (tracked as **CO-7**). |
| **win-x64 parallelism + networking coexistence (CO-6)** | The win-x64 per-thread arena is the TEB `ArbitraryUserPointer` scratch slot (`gs:0x28`), **not** PE thread-local storage, so — unlike arm64's real-ELF-`PT_TLS` arena (**CO-3**) — it does not collide with rustls's Windows TLS: Rust's std TLS goes through the standard PE `.tls` / `TlsAlloc` path (the TEB `ThreadLocalStoragePointer`), which never touches `ArbitraryUserPointer`. Consequently win-x64 keeps `both` genuinely forking in networking programs (the fork runtime is emitted unconditionally, with no networking gate). Empirically verified under Wine: a program that runs heavy `both` fork/join both before **and** after a full rustls loopback TLS handshake produces correct results on both sides with no crash/corruption (`tests/parallel_tls_coexist.ash`); the fork is genuine (~2.4× wall-clock speedup, two independent CPU-bound thunks: sequential ≈ 860 ms vs parallel ≈ 355 ms) and memory-bounded (peak RSS flat at ~27 MB across 300 → 30 000 outer iterations — ~2 M forks — on par with the linux-x64 baseline). No CO-3-style conflict exists on win-x64. |
| **Data-parallel `map`/`reduce` (CO-1)** | `Ashes.Parallel.map`/`reduce` (and the grain-parameterized `mapGrained`/`reduceGrained`) are now genuinely data-parallel via **call-site monomorphization**: above the grain threshold their bodies split the list in half and evaluate the two halves through `both`, and a saturated call at a concrete element type generates a monomorphic self-recursive specialization whose `both` splits see a concrete result and fork (at or below `grain` they run the sequential `plSeqMap`/`plSeqReduce`). Used polymorphically or partially applied they degrade to a correct sequential evaluation (the polymorphic copy, whose `both` sees an abstract result). The specialization references the module's top-level list helpers by-label (static code, empty env) so nothing arena-allocated crosses a fork. Verified deterministic (result identical to sequential, incl. heap-`Str` deep-copy on join) and memory-bounded on all three targets (linux-x64 native, linux-arm64 qemu, win-x64 wine). |
| **TCO back-edge reset of a relocated reuse accumulator (CO-8)** | The `Ashes.Map` "sorted-key SIGSEGV" was **misdiagnosed** as an AVL/balance bug and a stack overflow — both **disproven**: balancing is `O(log n)` (height 18 at 200k sorted keys) and direct sorted inserts of 200k keys run clean; the gdb backtrace showed 2–3 frames faulting on a garbage child pointer (corruption, not exhaustion). The real defect was a **use-after-free at the TCO back-edge plain arena reset**. An accumulator marked *reset-safe* (its in-place reuse specialization rewrites it below the loop watermark) was assumed address-stable from its param name alone, but the reset never checked that the **back-edge argument expression** actually preserved that address. When the value threaded back went through a nested reuse fold whose entry deep-copy was **not** elided (a retained/declined seed — CO-2's conservative case), the accumulator was a **copy relocated above** the watermark each iteration; the plain reset then freed the live tree, and the next round's deep-copy read the dangling source while bump-allocating over it → SIGSEGV (growing key sets shift the layout and expose it; a fixed key set survives by accident). Fix (`Lowering.cs`): the plain reset now additionally requires the back-edge argument to be **provably address-stable** — a bare accumulator Var (live-scope slot check), an in-place reuse call whose last arg is stable, or a call to a fold proven to thread its accumulator through at a stable address (elided entry copy + every tail leaf stable, recorded by definition span). When stability can't be proven, control falls through to the existing sound fallback (no reset; the arena grows for the loop's duration). Fully-elided nested reuse folds keep the fast reset (verified RSS-flat at 200k rounds). Regression: `tests/reuse_map_tco_reset_declined_seed.ash` (growing keys, retained seed — crashed pre-fix, prints `7` post-fix). |
| **TCO back-edge argument slot mis-mapping (CO-9)** | The reported "recursive ADT accumulator in a non-last curried position → SIGSEGV" was **misdiagnosed on the trigger** (a lone recursive/copy-field ADT in a non-last position is fine, at 1 iteration and at scale) but pointed at a **real, position-keyed TCO miscompile**. A recursive function's per-iteration parameter slots (`tco.ParamSlots`) were built in capture-**discovery** order (the order free variables first appear in the body), but the back-edge stored argument `i` into `ParamSlots[i]` assuming parameter-**declaration** order (as do the copy-out and CO-8's reset-stability check). When the two orders differ — e.g. `loop s xs n` whose body mentions `s` before `xs` while a string and a list are both threaded — the string and list pointers were written into each other's slots (a swap); the next iteration then read a list through the string slot and vice versa, corrupting **both** accumulators and crashing after a **single** back-edge. It only surfaced with two heap accumulators of different kinds in the "wrong" order (same-kind pairs and copy-type args happened to stay consistent), which is why it looked ADT/position-specific. Fix (`Lowering.cs`): build `ParamSlots` in parameter order by resolving each `tco.ParamNames[i]` through the loop-entry scope (the innermost param resolves to its arg slot, captured params to their freshly-bound locals), so `ParamSlots[i]` is always the i-th parameter's (and i-th back-edge argument's) slot. Proven with gdb: pre-fix the list slot held the string's address; the swap is eliminated. Regression: `tests/tco_multi_heap_accumulator_arg_order.ash` (string+list and string+reuse-ADT in reversed capture order — both SIGSEGV'd pre-fix, print `11 15` post-fix). |

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
| **CO-7** | **`both` does not temporally overlap an in-flight async task** | Re-investigated (see detailed analysis below); the original "`both` lowers to inline inside an async state machine" framing was a **misdiagnosis**. A concrete-result `Ashes.Parallel.both` **does** fork a worker in an async/networking program — verified on linux-x64 that a genuinely-forking `both` emits the identical `clone`/`lock xadd` fork runtime with or without `Ashes.Async.run` in the same program (`tests/parallel_async_coexist.ash`). The real, target-independent coupling is **temporal, not a lowering gate**: Ashes' async runtime is *eager and synchronous* (`async EXPR` = eager eval + `CreateCompletedTask`; `await`/`Ashes.Async.run` = `RunTask`, a blocking driver on the calling thread), and there is **no live suspending-coroutine path** (`StateMachineTransform` + its sole caller `LowerCapturedStringTask`, the only emitter of `CreateTask`/`CoroutineInfo`, are unreachable dead code). So a `both` fork runs *before or after* a blocking async I/O, never concurrently with one — and a `both` can never sit *inside* a state machine because no user code is lowered into one. Closing the temporal gap needs a genuine non-blocking async scheduler (wire up the dead state-machine path + an event loop), a much larger milestone. No correctness issue exists today (fork + async coexist and are memory-bounded). **Increment landed:** the state-machine transform's `Parallel*` liveness + fork/join/cleanup-within-one-segment invariant (the one blocker that was independently verifiable without the scheduler) is now implemented and directly unit-tested (`StateMachineTransformTests`), so it is no longer untested dead code. Remaining blockers: the non-blocking scheduler, wiring the coroutine path to user code, and the concurrent-overlap test. Design/validation + partial implementation task. See the **subtask decomposition** below. |

---

## Subtask decomposition (single-workload slices)

The remaining open roadmap item (CO-7) bottoms out on a milestone (a non-blocking async runtime), so it
does not close in one pass. The slices below each have a single failing→passing verification endpoint
sized for one focused work session, and together they cover the feature fully. Dependencies are stated;
the marked spine is the minimal fully-covering path. (CO-2's own decomposition is complete — all of its
slices landed; see the Completed Work table and the *CO-2 reference* section.)

### CO-7 remaining — genuine temporal overlap of `both` with in-flight async I/O

**Runtime-model decision (settled):** async I/O uses a **single-threaded cooperative event loop**;
`both` remains the CPU-parallelism primitive (real OS threads + per-thread arenas + deep-copy-on-join).
Rationale: a work-stealing pool would migrate a suspended coroutine across threads, but each thread has
its own bump arena, so migration breaks the per-thread-arena invariant or forces a deep copy per hop; a
single loop thread keeps every coroutine and its allocations on one arena. Cooperative is also
deterministic (matching `both`), and the split is clean — I/O concurrency on the loop, CPU parallelism
via `both`. A CPU-bound coroutine stalling the loop is exactly what `both` offloads.

| ID | Slice | Endpoint / verification | Deps | Risk |
|----|-------|-------------------------|------|------|
| **CO-7a-1** | **Make the coroutine path live (semantics-preserving).** Wire `LowerCapturedStringTask` / `CreateTask` / `StateMachineTransform` so user `async`/`await` lowers to a suspending coroutine, while `RunTask` stays a *blocking* driver — behavior identical, but now through the state machine (turns today's dead code live). | Existing async tests pass, now routed through the coroutine path (a transform-coverage assertion / binary byte-scan proves the state machine is exercised). | — (builds on done blocker 3) | med (codegen + coroutine ABI) |
| **CO-7a-2** | **Cooperative scheduler + timer queue (non-networking).** Replace the blocking driver with a cooperative loop over a ready/timer queue; make `Ashes.Async.sleep` a real timer suspension. | `tests/async_sleep_interleave.ash`: two tasks provably interleave (B advances while A sleeps) — impossible today. | CO-7a-1 | med–high |
| **CO-7b-1** | **linux epoll networking leaf tasks.** Make `CreateHttpGetTask` / `CreateTls*Task` non-blocking on linux, integrated so an I/O-pending task yields to the loop. | Loopback HTTPS where a second task runs while the request is in flight. | CO-7a-2 | high (per-platform I/O) |
| **CO-7b-2** | **win-x64 IOCP / overlapped networking.** Same for Windows, verified under Wine. | Wine loopback overlap test. | CO-7a-2 | high |
| **CO-7b-3** | **linux-arm64 networking.** Mostly epoll reuse + qemu validation (small; may fold into CO-7b-1). | qemu loopback overlap test. | CO-7b-1 | low–med |
| **CO-7c** | **`both` ↔ scheduler overlap + concurrent-overlap test (blocker 4).** Ensure a `both` inside an async segment yields to the loop while an I/O is pending; add the test that observes a worker making progress during a live I/O. | The concurrent-overlap test the CO-7 blocker list names. | CO-7a-2, CO-7b-1 | med |

**Fully-covering spine:** CO-7a-1 → CO-7a-2 → CO-7b-1 → CO-7c (CO-7b-2 / CO-7b-3 are cross-target completeness).

---

### CO-2 — reference (move/linearity reuse-copy elision)

**Fully implemented** (see the Completed Work table). This section records the mechanism, the soundness
argument, and the regression tests; the code is `Lowering.MoveAnalysis.cs`.

**Where the copy is.** `Lowering.LowerLambda` (innermost-TCO branch) emits a one-time defensive deep copy
of each in-place-reused accumulator into the function prologue — for the *direct-reuse* path (the loop
body rebuilds the accumulator with the same constructor) and the *specialization* path (the accumulator
is threaded into a specializable recursive function `f` cloned to `f$reuse`). Its sole role is to hand
the reuse machinery a **uniquely-owned** accumulator to overwrite. When an outer loop threads a *growing*
accumulator into an inner reuse fold, that prologue copy re-executes per re-entry and lands in the
never-reset to-space — an `O(re-entries × size)` leak. Eliding the copy when the accumulator is *already*
uniquely owned removes the leak and reproduces the machinery's exact precondition, so it needs no
reuse-internals reasoning.

**Move predicate** (`IsReuseAccumulatorMoveSafe` → `IsParamMoveSafe`, on-demand greatest fixpoint;
conservative default = keep the copy). A fold's accumulator parameter is move-safe iff the fold's name
never escapes as a value (a complete call-site census) and, at every external call site, the argument is
a **move**:

- a **sole-nullary-constructor seed** — its cell holds only a tag; reuse rewrites the identical tag (a
  no-op even when shared);
- a **fully-fresh construction** — no variable reference except sole-nullary leaves, so unique by
  construction;
- a **move-linear reference** to a move-safe accumulator parameter of the enclosing function (used at
  most once on any path, never captured — the transitive, interprocedural step);
- a **`let`-bound fresh value proven dead-after-use** (fresh RHS + move-linear in the binding's scope);
- a **registered-function call admitted by the result-reachability summary** (below).

**Result-reachability (may-alias) summary** (`ComputeResultReach`, a monotone least fixpoint over all
registered functions). Per function it over-approximates which of *its own parameters* the result may
alias — a per-parameter multiplicity capped at 2 — plus a **poison** flag ("result not provably confined
to parameters"). Structural transfer: literals / copy-typed scalars reach {}; a bare parameter reaches
{itself}; a sole-nullary constructor reaches {}; a global/top-level reference, a non-sole nullary, a
partial application, or an unmodeled node **poisons**; `if`/`match` join arms by **max** (a match's
scrutinee reach flows into its pattern bindings); a constructor application **sums** its **heap-typed**
fields (copy-typed fields hold inline scalars — ignored); a call substitutes the callee's reached
parameters with the argument reaches. **Summing is what makes internal sharing poison** — a cell reachable
through two simultaneously-live positions is exactly what the entry copy exists to unshare.

Four refinements make it precise enough for real code without ever under-claiming reach:

- **Hierarchical path tokens** (CO-2c): destructuring field `i` yields a *distinct sub-cell* token `k/i`,
  so a destructure-then-rebuild helper (`balance`, `makeNode(left)(key)(value)(right)`) re-embedding a
  value's *disjoint* children reaches `{map}` cleanly instead of a false `{map:2}`. Poison still fires on
  the cap (same cell twice) or a proper path **ancestor/descendant** pair (a value co-embedded with its
  own sub-cell). Stored summaries collapse tokens to root parameter names.
- **Per-binding identity tokens**: every `let`/pattern binding also gets a fresh `'#'`-rooted token, so a
  *fresh* (non-parameter) value embedded twice (`let x = … in Node(x)(0)(x)`, `[x, x]`, …) still poisons;
  distinct bindings get distinct tokens (disjoint sub-values stay disjoint); a bare `Var` pattern naming
  a constructor (`| Empty ->`) binds nothing.
- **Nested-rec-return registration** (CO-2c): the `Ashes.Map.set` shape (`fun … -> let rec go = fun acc
  -> B in go`) is registered with the accumulator as a real trailing parameter and its `go(x)` self-call
  resolved against the function's own growing summary, so `set`'s result reaches `{map, newKey, newValue}`.
- **Over-application reach** (CO-2d): a closure the currying did not flatten (a lambda returned from
  behind `if`/`match`/`let`) applied to surplus arguments is modeled by inlining the callee one level and
  binding surplus args to the returned lambda's parameters — capture-aware, over `"@i"` argument markers.

**Wiring** (`IsResultAliasMove`): a saturated call `f(a₁…aₙ)` is a move iff `f`'s summary is not poisoned
and, for every parameter its result may reach, the argument bound to it is itself a move (recursively).
The empty-reach case is a **result-fresh** function — a move for any arguments — covering recursive
builders (`let rec build n = if n <= 0 then Leaf else Node(build(n - 1))(n)(Leaf)`); a non-empty reach
covers `wrap`-style and `Map.set`-shape builders when the reached arguments are moves.

**Soundness.** The elision reproduces the reuse machinery's precondition (a uniquely-owned accumulator).
The load-bearing invariant is that **a value admitted as a move is a proper tree** (the move rules poison
internal sharing), so path-disjointness ⟹ cell-disjointness at every elision site. The summary is a sound
over-approximation with a conservative default (poison → keep the copy), so every unproven or unmodeled
shape is a *leak, never a corruption*. The direct-reuse path additionally keeps the *pure-reader* guard
(a move-safe fold that only reuses a dead nullary leaf reverts that reuse to a fresh allocation).

**Tests** (all green; each retained/aliased sibling is correctly *declined*, reading its uncorrupted
value): `reuse_move_elision_soundness.ash`, `reuse_direct_move_elision.ash`,
`reuse_letbound_fresh_move_elision.ash`, `reuse_higher_order_seed_move_elision.ash`,
`reuse_result_alias_move_elision.ash`, `reuse_internal_sharing_declines.ash`,
`reuse_closure_seed_move_elision.ash`, `reuse_map_set_seed_move_elision.ash`,
`reuse_map_set_seed_retained_declines.ash`, and the correctness guard `reuse_nested_reentry_correct.ash`.

**Measured wins.** Nested `Ashes.Map.set` re-entry `O(batches × size)` → constant (was 8→54 MB across
400–3200 batches). Recursive-builder-seeded direct-reuse fold (32 767-node tree × 400 batches):
504 MB → 4.7 MB (the threaded accumulator persists, so the per-batch entry copy is a genuine
`O(batches × size)` term). `Ashes.Map.set`-result-seeded fold: ≈2× (200 000-key base × 4000 rounds,
43 → 22 MB). Direct-reuse / richer-aliasing seeds remove only a constant-size startup copy (a direct-reuse
accumulator cannot grow, so there is no `O(batches × size)` term there).

### CO-7 — detailed analysis (`both` vs. the async runtime)

**Status: design/validation complete; the original diagnosis was wrong. Blocker (3) — the
`StateMachineTransform` liveness/invariant work — is now implemented and directly unit-tested (see
below); the remaining blockers (1) the non-blocking scheduler, (2) wiring the dead coroutine path,
and (4) the concurrent-overlap test are still a milestone-sized runtime, and no *runtime* increment
(anything a compiled program observes) is safe-and-verifiable until (1)/(2) exist.** This section records the
mechanism as it actually is on current `main`, corrects the roadmap's earlier "`both` lowers to
inline inside an async state machine" claim, and lists the precise blockers.

**Mechanism as built.**

- *`Ashes.Parallel.both` lowering* (`Lowering.LowerParallelBoth`) is **async-agnostic**. It emits
  `ParallelFork` / `ParallelJoin` / `ParallelCleanup` whenever `CanRunRightOnWorker(bType)` holds —
  i.e. the right thunk's result is a concrete, deep-copyable type (scalar / `Str` / `Bytes` /
  arena-reset list / tuple / non-resource ADT). An abstract (still-a-type-variable) result — e.g. a
  self-recursive `both` whose return type is unresolved during lowering, as in `psum` — takes the
  sequential `else` branch. This has nothing to do with async; it is the documented concrete-result
  gate of the parallelism feature itself.
- *The backend emits the fork runtime* (worker trampoline + active-worker counter,
  `EmitParallelRuntime`) **iff** `ProgramUsesInstruction<IrInst.ParallelFork>(program)` — again with
  no networking/async gate (arm64's arena is now always on, per CO-3; win-x64 never gated, per CO-6).
- *The async runtime is eager and synchronous.* `async EXPR` = `Ashes.Async.task(EXPR)` lowers `EXPR`
  **eagerly** and wraps the value in `CreateCompletedTask` — it does **not** build a coroutine.
  `await` / `Ashes.Async.run` lower to `RunTask`, whose codegen (`EmitRunTask`) is a **blocking**
  driver loop on the calling thread: leaf networking tasks (`CreateHttpGetTask`, `CreateTls*Task`, …)
  are stepped and *waited on* inline (`EmitWaitForPendingLeafTask`).
- *The suspending-coroutine path is dead code.* `StateMachineTransform.Transform` is called from
  exactly one place, `Lowering.LowerCapturedStringTask`, which is **never called** — so `CreateTask`
  and `CoroutineInfo` are never produced by any user lowering. The multi-state `stepBlock` of
  `EmitRunTask` is therefore unreachable from user programs; only the leaf-task branch runs. No user
  code is ever lowered *into* a state machine.

**What this means for the roadmap claim.** "Any `both` reachable from an async state machine lowers
to inline" is **not reproducible**, because (a) no user code is in a state machine, and (b) a
concrete-result `both` forks regardless of async usage. Verified on linux-x64 by byte-scanning
compiled binaries for the clone-flags immediate (`0x00250f00`) and the `lock xaddq` opcode
(`f0 48 0f c1`) — the fork runtime's fingerprints:

| program | forks? |
|---|---|
| concrete `both` alone | yes |
| concrete `both` + `Ashes.Async.run(Ashes.Async.sleep 1)` | **yes (identical fork runtime)** |
| concrete `both` lexically inside `async(let x = await … in …)` | **yes** |
| self-recursive `psum` `both` (abstract result), async or not | no — concrete-result gate, *not* async |

So the genuine, target-independent coupling is **temporal**: because the async runtime blocks the
calling thread for the entire duration of a "live" TLS/HTTP session, a `both` fork happens strictly
*before or after* that I/O, never *concurrently* with it. `tests/parallel_tls_coexist.ash` (CO-6)
and the new `tests/parallel_async_coexist.ash` both show `both` forking around — but not during — an
async I/O, correct and memory-bounded.

**Can a fork be safely interleaved with the async runtime today?** The arena/TLS *layers* are already
proven to coexist: a worker gets its own per-thread bump arena (GS-segment TCB on linux-x64, TEB
`ArbitraryUserPointer` on win-x64, ELF `PT_TLS` local-exec cursors on linux-arm64 — CO-3/CO-6), and
the right thunk is a pure closure whose result `CanRunRightOnWorker` restricts to arena-safe
deep-copyable types, so it never aliases the main thread's rustls/socket state. The blocker is not
safety of the arena — it is that **there is nothing to interleave with**: `RunTask` is synchronous,
so there is no in-flight async task in the sense of a concurrently-progressing I/O while the main
thread runs other code.

**Why no safe verifiable increment was landed.**

1. *Making a fork overlap in-flight I/O* requires a real non-blocking async scheduler: wire up the
   currently-dead `StateMachineTransform` / `CreateTask` / `LowerCapturedStringTask` path, make the
   networking leaf tasks non-blocking (epoll/IOCP/kqueue event loop), and let `RunTask` drive
   suspensions cooperatively so the main thread can advance a `both` while an I/O is pending. That is
   a milestone-sized runtime, not a conservative increment.
2. *Preserving `both`'s forkable shape through the state-machine transform* is a **precondition** of
   (1). It has two parts, **both now implemented and directly unit-tested** (`StateMachineTransformTests`
   drives `StateMachineTransform.Transform` on hand-built IR — the transform is still unreachable from
   user programs, but these changes are no longer *untested* dead code):
   **(a)** `StateMachineTransform`'s liveness tables (`GetDefinedTemps` / `GetUsedTemps`) now know
   `ParallelFork` (defines `DescTarget`, uses `RightClosureTemp`), `ParallelJoin` (defines
   `ResultTarget`, uses `DescTemp`), and `ParallelCleanup` (uses `DescTemp`) — so a `both` descriptor,
   joined result, or right-thunk closure live across an `await` split is now saved/restored instead of
   dropped. Two tests assert the save/restore actually happens (each fails if its liveness case is
   removed). **(b)** `Transform` now asserts (via `AssertParallelForkJoinWithinSegment`) that a `both`'s
   fork→join→cleanup stays **within a single coroutine segment** (no `await` between a `ParallelFork`
   and its `ParallelCleanup`), because the worker descriptor and worker arena are not part of the
   serialized state struct and cannot survive a suspend/resume. `LowerParallelBoth` emits
   fork+join+cleanup contiguously with no intervening `await`, so this holds by construction — the
   assertion turns that contract into a hard invariant (a test feeds a fork/await/cleanup shape and
   asserts it throws; the contiguous shape does not). This unblocks (2) for the parallelism side, but
   the coroutine path is still not *reachable* from user code — that is blocker (2)-proper below.

**Verifiable increment that *was* landed.** `tests/parallel_async_coexist.ash`: a portable (no
loopback server) regression that runs two genuinely-forking concrete-result `both`s (heavy
`sumRange` halves) before and after a synchronous `Ashes.Async.run` await, asserting every result
equals the sequential value. It locks in the *correct* current behavior — fork runtime and async
runtime coexist and produce correct, memory-bounded results on every target — and guards against a
regression that would make async usage suppress the fork. (It cannot assert temporal overlap, which
is impossible without the scheduler above.)

**Remaining CO-7 work (blocker list).**

1. A non-blocking async scheduler / event loop and non-blocking networking leaf tasks (the enabling
   milestone; everything else depends on it). **Model settled: a single-threaded cooperative event
   loop** (see the *Subtask decomposition* above for the rationale vs. a thread pool). Sliced into
   **CO-7a-2** (cooperative scheduler + timer queue) and **CO-7b-1/2/3** (per-target non-blocking
   networking leaf tasks).
2. Wire the dead `StateMachineTransform` / `CreateTask` / `LowerCapturedStringTask` path so user
   `async` code lowers to a genuine suspending coroutine. Sliced as **CO-7a-1** (make the coroutine
   path live, semantics-preserving).
3. ~~Extend `StateMachineTransform` liveness to the three `Parallel*` instructions (blocker-(2)(a)
   above) and assert the fork→join→cleanup-within-one-segment invariant (blocker-(2)(b)).~~
   **DONE** — `GetDefinedTemps`/`GetUsedTemps` now cover `ParallelFork`/`ParallelJoin`/`ParallelCleanup`,
   and `Transform` asserts the within-one-segment invariant. Directly unit-tested by
   `StateMachineTransformTests` (each liveness assertion fails if its case is removed; the invariant
   test asserts a fork/await/cleanup shape throws). This is the one increment that was *both* real
   work on the remaining blockers *and* independently verifiable without the scheduler.
4. A test that observes *actual concurrent overlap* (a `both` worker making progress while an async
   I/O is pending) — only meaningful once 1–2 exist.
