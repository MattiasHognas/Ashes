# Resume Here — Parallelism (#5) & In-Place Reuse (#2)

**One-stop handoff.** Two milestones are partly built; this is the single place that
says what's done, what's left, and exactly where to start. Point me at this file.

> **Fastest way to resume:** "Read docs/future/RESUME_HERE.md and continue with the
> next step." The next concrete step is in **§3**.

> **STATUS 2026-06-30:** #5 (`both` parallelism) is **DONE & verified on linux-x64** — see
> §1. The only milestone left here is **#2 (in-place reuse)**; jump to §2 → §3.

Full designs (already signed off): `STRUCTURED_PARALLELISM.md` (#5),
`REUSE_ANALYSIS.md` (#2). User-facing flaw context: `../../challenges/FLAWS.md`.

---

## 1. DONE (landed, tested, tree green: 1306 unit + 348 e2e + 52 LSP, `dotnet format` clean)

- **Shared deep-copy foundation** — the hard piece both milestones depend on.
  - `EmitDeepCopy(temp, type)` + `TrySynthesizeAdtCopier` + `CopyFieldInsideCopier` in
    `src/Ashes.Semantics/Lowering.Ownership.cs` (~line 452+).
  - Handles scalars, `Str`/`Bytes`, tuples, lists, closures, **and recursive
    multi-constructor ADTs** (`Map`/`HashMap`/`Maybe`) via a synthesized, cached,
    self-referential copier closure (tag-level; `env[0]` = self; resource types excluded;
    mutual-recursion cycle guard).
  - Exposed/tested via the intrinsic `Ashes.Internal.deepCopy : ∀a. a→a`
    (`tests/internal_deepcopy.ash`, `tests/internal_deepcopy_recursive.ash`).
- **#5 API layer** — `lib/Ashes/Parallel.ash` (`both`/`map`/`reduce`), deterministic,
  pure Ashes, **sequential for now**. Embedded in `Ashes.Semantics.csproj`, registered in
  `BuiltinRegistry` (`Ashes.Parallel`). Test: `tests/stdlib_parallel.ash`.
- **#5 step 1 — freestanding per-thread TLS arena (linux-x64) — DONE & verified 2026-06-30.**
  The gating blocker. *Deviates from the "thread_local + PT_TLS" plan in §2 — simpler, and
  needs zero custom-linker TLS-relocation work:*
  - The bump-arena cursor/end live in a per-thread control block (TCB) reached through the
    **GS** segment base (`arch_prctl(ARCH_SET_GS=0x1001, &__ashes_main_tcb)` in the entry
    prologue). GS, not FS: glibc + the dlopen'd rustls runtime own FS for their own
    thread-locals; GS is free for application use on x86-64 Linux.
  - TCB layout: offset 0 = self-pointer, 8 = cursor, 16 = end (`MainTcbSizeBytes` = 512, BSS).
  - **Arena is never addressed through an `addrspace(256)` pointer** — that tripped a real
    O0 FastISel miscompile (a value loaded from `%gs:0` then used via `inttoptr`+store got a
    spurious `%gs:` segment override; confirmed by gdb watchpoint; O1+/SelectionDAG is fine).
    Instead each function recovers the TCB base via opaque inline asm `movq %gs:0, $0`
    (no side effects → hoistable/CSE-able) and addresses cursor/end as ordinary pointers.
    Entry uses `&__ashes_main_tcb` directly (avoids ordering before GS is set).
  - Helpers in `src/Ashes.Backend/Llvm/LlvmCodegenMemory.cs`: `EmitMainThreadTlsInit`,
    `EmitReadTcbBaseFromGs`, `BuildLinuxArenaSlots`, `WithLinuxThreadArena`. **Three**
    state-construction sites apply them: `EmitFunctionBody` (LlvmCodegen.cs) + the two
    runtime-ABI helpers `EmitRuntimeFunction`/`EmitInternalRuntimeFunction`
    (LlvmCodegenBuiltins.cs) — the test harness emits the full networking ABI un-tree-shaken,
    so missing any one site = null `HeapCursorSlot` → verify failure on ~every backend test.
  - Other flavors (arm64, win-x64) keep plain module globals (`__ashes_heap_cursor`/`_end`),
    single-threaded — unchanged. Constants added in LlvmCodegen.cs: `SyscallClone=56`,
    `SyscallFutex=202`, `SyscallArchPrctl=158`, `ArchSetGs=0x1001`,
    `FutexWaitPrivate/WakePrivate=128/129`.

## 2. NOT DONE (the high-risk runtime cores — left deliberately, not half-built)

Both were left unbuilt because they're unsafe to write unattended (silent memory
corruption / data races, single-platform-testable). Each now sits on the finished
deep-copy foundation.

### #5 — threading runtime — ✅ DONE & verified (linux-x64), 2026-06-30

`Ashes.Parallel.both` is genuinely parallel. Steps 1–4 below are done; step 5 is moot.
See §1 for the arena foundation and `LlvmCodegenParallel.cs` for the worker runtime
(clone/futex, deep-copy-on-join, `lock xadd` worker cap = 8). Verified: real ~2x speedup
(198% CPU), deterministic, memory-bounded (9.4 MB over 3000 iters), stable across thousands
of runs incl. nested divide-and-conquer; full suite green (1306 unit + 349 e2e).

- `both` is a hybrid-module intrinsic (`BuiltinValueKind.ParallelBoth`) lowered per call site;
  concrete result type → `ParallelFork`/`CallClosure(left)`/`ParallelJoin`/`EmitDeepCopy`/
  `ParallelCleanup`/tuple; abstract/closure result → sequential fallback (`CanRunRightOnWorker`).
- **Step 5 (data-parallel `map`/`reduce`) is NOT possible without monomorphization:** their
  element type is an abstract type variable inside the polymorphic body, and deep-copy-on-join
  needs the concrete type. They stay sequential; parallelism is via direct concrete `both`
  (e.g. shard-and-merge with explicit `both` at `Map`/list/scalar result types).
- Known minor gap: worker `mmap`s are unchecked (crash under an artificial `ulimit -v`, same
  assumption as the main arena) — could fall back to inline on `MAP_FAILED`. Deferred.

### #2 — in-place reuse fast path (fix the hot-loop arena leak, fast) — ⭐ the remaining work

The user chose _automatic in-place reuse, no new syntax_ (not the slow deep-copy stopgap).
Needs:

1. **Interprocedural linearity analysis** (`Lowering.Ownership.cs`): prove a value is
   consumed exactly once (the TCO accumulator threaded through `loop(newAcc)` is the target).
2. **Reuse tokens**: a `match` that deconstructs a linear ADT yields its dead cell; a
   same-size `AllocAdt` consumes it (new IR `AllocReusing`) and writes in place.
3. **TCO arena-reset integration** (the delicate, region-aware step): extend
   `GetTcoCopyOutKind`/`CanArenaReset` (`Lowering.Ownership.cs`) so a linear+reused
   accumulator lets the back-edge reset the arena. _Deep-copy fallback already exists for
   non-linear cases._

> **STATUS 2026-06-30 — investigated deeply; both viable paths are larger than a safe
> unattended change. Confirmed the leak: 200K `Map.set` calls over 50 keys → VmPeak 515 MB
> (the superseded path nodes pile up because the back-edge can't reset).**
>
> **Path 1 — fast in-place reuse (what the user wants).** Needs the reuse to thread *through*
> `Map.set`'s recursion + `balance`/`rotate` (interprocedural). Without runtime refcounting
> (Ground Rule 6), soundness needs **linearity-driven *specialization*** — a "linear" clone of
> `Map.set` (and its helpers) that reuses, called only where the arg is provably linear (the TCO
> accumulator). A wrong inference is silent use-after-reuse corruption along a rebalancing path.
> This is the genuine Perceus-without-RC feature; substantial and corruption-prone.
>
> **Path 2 — deep-copy fallback (the doc's Phase 4; user de-prioritized).** I **attempted wiring
> it into the TCO two-pass** (`GetTcoCopyOutKind` → new `DeepCopy` kind, `EmitDeepCopy` in both
> phases) and **it segfaults — fundamental flaw, reverted.** The two-pass copies each arg UP to
> `[C1, C1+K]` then DOWN to `[W, W+K]`; for a *fixed-size* shallow copy these don't overlap, but a
> `Map.set` iteration allocates only **O(log K)** new nodes, so `C1 − W ≪ K` and the down-copy
> `[W, W+K]` **overlaps and clobbers the up-copy mid-walk**. A correct deep-copy fallback therefore
> needs a **separate scratch / persistent region** (to-space) so the copy's source and destination
> never share the resettable arena — i.e. deep-copy the new map into a fresh `mmap`'d region, reset
> the main arena, keep the accumulator in (or copy it back from) the separate region, free the old
> one. That's a new arena-switch mechanism (~100–150 lines), still O(K)/iter, but it *would* end
> the OOM safely. The faster Path 1 is the real goal.

## 3. ⭐ NEXT STEP (start here)

#5 (**done**) and all of `RESOURCE_SAFETY.md` (**done**, Gaps A–D + deterministic close) are
landed. The only remaining milestone is **#2 — in-place reuse** (§2), now investigated in depth.

Pick the path (see §2 STATUS):
- **Fast (Path 1):** linearity-driven specialization of `Map.set`. Start with the linearity
  analysis as a **read-only pass** that *reports* which TCO accumulators are provably
  consumed-exactly-once, verified on `tests/` folds; then a "linear" specialized clone of
  `Map.set`/`balance`/`rotate` with reuse tokens (`AllocReusing`) + TCO arena-reset integration.
  Gate behind a reuse-correctness + constant-memory + shared-accumulator-falls-back test trio.
- **Safe bounded-memory (Path 2):** the **separate-scratch-arena** deep-copy fallback (the naive
  in-arena two-pass deep copy was tried and *segfaults* — see §2). Add an arena-switch so the
  per-iteration deep copy goes to a fresh `mmap`'d region disjoint from the resettable arena.

The move/linearity machinery already partly exists from resource safety (`IsResourceBearing`,
`MarkResourceArgMoved`, the TCO back-edge move/drop, `SynthesizeClosureResourceDropper`).
