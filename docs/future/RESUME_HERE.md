# Resume Here — Parallelism (#5) & In-Place Reuse (#2)

**One-stop handoff.** Two milestones are partly built; this is the single place that
says what's done, what's left, and exactly where to start. Point me at this file.

> **Fastest way to resume:** "Read docs/future/RESUME_HERE.md and continue with the
> next step." The next concrete step is in **§3**.

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

### #5 — threading runtime (make `both` actually parallel)

Make `Ashes.Parallel.both` run its two thunks on real threads. Remaining steps:

1. ✅ **Freestanding per-thread arena — DONE** (see §1; GS-segment TCB, not the original
   `thread_local`/`PT_TLS` plan). Single-thread suite green end to end.
2. `clone(2)` + `futex` spawn/join (Linux x64 first), `CreateThread`/`WaitForSingleObject`
   (Windows) — add to the syscall/import layer like the existing file/socket ops. **The hard
   freestanding part:** after a raw `clone`, the child runs the *next instruction* on the new
   stack — branch immediately to a worker trampoline that (a) `arch_prctl(ARCH_SET_GS,
   worker_tcb)` + stores the worker TCB self-pointer, (b) inits a fresh heap chunk
   (`EmitHeapChunkInit`-equivalent on the worker arena), (c) calls the `right` thunk closure,
   (d) writes the result pointer into a shared descriptor, (e) `futex` wake on a done-word,
   (f) `exit` — never returns up the (parent's) stack. Pass the task descriptor through a
   register the clone asm doesn't clobber.
3. A `both` **intrinsic** (replace the pure-Ashes `both`) — new IR inst (e.g.
   `ParallelBoth`) — that forks `right` to a worker, runs `left` inline, `futex`-waits the
   done-word, then **deep-copies the worker's result into the parent arena via the existing
   `EmitDeepCopy`** (emit the copy in lowering, after the join), then `munmap`s the worker
   arena chunks. Ordering is the delicate part: copy must run after the worker is done and
   before the worker arena is freed.
4. Bounded workers: a shared atomic active-worker counter capped at CPU count; when at the
   cap `both` runs inline (graceful degradation — already correct, since the pure-Ashes
   fallback is sequential). A real pool is an optimization on top.
5. Rewrite `map`/`reduce` in `Parallel.ash` to fork through the `both` intrinsic.

> ⚠️ Risk note (why this is still deferred): a freestanding child-on-new-stack thread runtime
> with per-worker arena bring-up and deep-copy-on-join is exactly the silent-corruption-prone
> core the original plan flagged. Build it behind the inline fallback and verify a
> parallel-==-sequential determinism test at scale before trusting it.

### #2 — in-place reuse fast path (fix the hot-loop arena leak, fast)

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

## 3. ⭐ NEXT STEP (start here)

Step 1 (the gating TLS-arena blocker) is **done and verified**. The next concrete step is
**#5 step 2 — `clone`/`futex` spawn+join on linux-x64**, then step 3 (the `both` intrinsic +
deep-copy-on-join). Build behind the inline/sequential fallback and gate progress on a
parallel-`==`-sequential determinism test (`tests/stdlib_parallel.ash` already checks
`map`/`reduce` results; add a larger-scale variant once `both` actually forks).

Alternative if you'd rather de-risk: do **#2** first — start with the linearity analysis as
a read-only pass that just *reports* which TCO accumulators are provably linear, before
touching any codegen.

Keep each sub-step green before the next. The deep-copy-on-join (#5 step 3) is already in
hand via `EmitDeepCopy`.

_(Alternative if you'd rather do #2 first: start with the linearity analysis as a read-only
pass and dump which TCO accumulators are provably linear, before touching any codegen.)_
